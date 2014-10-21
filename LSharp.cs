/*
** This source file is the implementation of L#
**
** For the latest info, see https://github.com/paladin-t/l_sharp
**
** Copyright (c) 2012 - 2014 Tony Wang
**
** Permission is hereby granted, free of charge, to any person obtaining a copy of
** this software and associated documentation files (the "Software"), to deal in
** the Software without restriction, including without limitation the rights to
** use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
** the Software, and to permit persons to whom the Software is furnished to do so,
** subject to the following conditions:
**
** The above copyright notice and this permission notice shall be included in all
** copies or substantial portions of the Software.
**
** THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
** IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
** FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
** COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
** IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
** CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace tony
{
    using number = System.Single;

    public class LSharp
    {
        #region Types

        public delegate void PrinterFunc(string txt);

        public delegate void ScopePoppedEventHandler(Scope scope);

        #endregion

        #region Constants

        public static readonly UInt32 VERSION = 0x0100000a;
        public static readonly string VERSION_STRING = "1.0.10";

        #endregion

        #region Enums

        private enum ParsingState
        {
            Common,
            String0,
            String1,
            Comment
        }

        #endregion

        #region Invokable

        public delegate object HostFuncDelegate(RuntimeContext context);

        public class HostFunc
        {
            private object target = null;
            private object[] context = null;

            public MethodInfo MethodInfo { get; private set; }
            public bool Normalized { get; private set; }

            public HostFunc(MethodInfo mi, object tgt, RuntimeContext ctx, bool normalized)
            {
                MethodInfo = mi;
                target = tgt;
                context = new object[] { ctx };
                Normalized = normalized;
            }

            public object Invoke()
            {
                return Invoke(context);
            }

            public object Invoke(params object[] args)
            {
                try
                {
                    return MethodInfo.Invoke(target, args);
                }
                catch (Exception ex)
                {
                    throw ex.InnerException;
                }
            }
        }

        public class HostFuncDict : Dictionary<string, HostFunc>
        {
        }

        #endregion

        #region Contexts

        private class ParsingContext
        {
            private struct StackNode
            {
                public SExp Exp;
                public ParsingState State;
                public StackNode(SExp exp, ParsingState ps)
                {
                    Exp = exp;
                    State = ps;
                }
            }

            private string token = null;

            private Stack<StackNode> stack = null;

            public bool Quot { get; set; }

            public ParsingState State { get; private set; }

            public ParsingContext()
            {
                token = string.Empty;
                stack = new Stack<StackNode>();
                Quot = false;
                State = ParsingState.Common;
            }

            public void AppendChar(char c)
            {
                token = token + c;
            }

            public SExp Cut(RuntimeContext context)
            {
                token = token.Trim(' ', '\t', '\r', '\n');
                if (token == string.Empty)
                    return null;

                number n = default(number);
                if (context.HostFuncs.ContainsKey(token))
                {
                    if (Quot) throw new IdExpectedException("ID expected, but got func", -1, -1);

                    return new SExpHostFunc(context);
                }
                else if (TryParse(token, out n))
                {
                    if (Quot) throw new IdExpectedException("ID expected, but got number", -1, -1);

                    return new SExpNumber(context);
                }
                else
                {
                    return new SExpId(context, false);
                }
            }

            public void Push(SExp s, ParsingState st)
            {
                if (Quot && s is IQuotable)
                {
                    Quot = false;
                    ((IQuotable)s).Quot = true;
                }

                if (stack.Count != 0)
                {
                    Assert(stack.Peek().Exp.Type == SExp.Types.List, "Invalid s-exp type");

                    stack.Peek().Exp.As<SExpList>().Add(s);
                }

                stack.Push(new StackNode(s, st));

                State = st;
            }

            public void Push()
            {
                State = ParsingState.Comment;
            }

            public SExp Pop()
            {
                if (State == ParsingState.String0 || State == ParsingState.String1)
                {
                    State = ParsingState.Common;

                    string[] splitted = token.Split('\\');
                    if (splitted.Length != 1)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.Append(splitted.First());
                        for (int i = 1; i < splitted.Length; i++)
                        {
                            string s = splitted[i];
                            switch (s[0])
                            {
                                case '"':
                                    sb.Append('"');
                                    break;
                                case '\'':
                                    sb.Append('\'');
                                    break;
                                case '\\':
                                    sb.Append('\\');
                                    break;
                                case 't':
                                    sb.Append('\t');
                                    break;
                                case 'b':
                                    sb.Append('\b');
                                    break;
                                case 'f':
                                    sb.Append('\f');
                                    break;
                                case 'n':
                                    sb.Append('\n');
                                    break;
                                case 'r':
                                    sb.Append('\r');
                                    break;
                                default:
                                    throw new UnknownEscapeCharException("Unknown escape charactor: " + s[0]);
                            }
                            sb.Append(s.Substring(1));
                        }
                        token = sb.ToString();
                    }
                }
                else if (State == ParsingState.Comment)
                {
                    State = ParsingState.Common;

                    return null;
                }

                SExp result = stack.Pop().Exp;

                State = stack.Peek().State;

                result.Take(ref token);

                return result;
            }
        }

        public class RuntimeContext
        {
            private Stack<SExp> currentEvaluating = null;
            private Stack<Counter> currentEnumerator = null;
            private Scope globalScope = null;
            private ScopeChain scopeChain = null;
            private HostFuncDict hostFuncs = null;
            private List<string> imported = null;
            private Dictionary<string, Type> importedTypes = null;
            private bool sending = false;

            public LSharp Interpreter { get; private set; }

            public Stack<SExp> CurrentEvaluating
            {
                get { return currentEvaluating; }
            }

            public Stack<Counter> CurrentEnumerator
            {
                get { return currentEnumerator; }
            }

            public Scope GlobalScope
            {
                get { return globalScope; }
            }

            public Scope LastScope
            {
                get { return scopeChain.LastOrDefault(); }
            }

            public IEnumerable<Scope> LastTwoScopes
            {
                get { return scopeChain.Skip(scopeChain.Count - 2); }
            }

            public IEnumerable<Scope> LastThreeScopes
            {
                get { return scopeChain.Skip(scopeChain.Count - 3); }
            }

            public int ScopeCount
            {
                get { return scopeChain.Count; }
            }

            public HostFuncDict HostFuncs
            {
                get { return hostFuncs; }
            }

            public List<string> Imported
            {
                get { return imported; }
            }

            public Dictionary<string, Type> ImportedTypes
            {
                get { return importedTypes; }
            }

            public bool Sending
            {
                get { return sending; }
                set { sending = value; }
            }

            public int _IndentCount { get; set; }

            public bool _Indent { get; set; }

            public event ScopePoppedEventHandler ScopePopped;

            public RuntimeContext(LSharp interpreter)
            {
                Interpreter = interpreter;

                currentEvaluating = new Stack<SExp>();
                currentEnumerator = new Stack<Counter>();

                globalScope = new Scope(null);
                scopeChain = new ScopeChain();
                PushScope(globalScope);
                PushScope();

                hostFuncs = new HostFuncDict();

                imported = new List<string>();
                importedTypes = new Dictionary<string, Type>();

                _IndentCount = 0;
                _Indent = true;
            }

            public void Clear()
            {
                currentEvaluating.Clear();
                currentEnumerator.Clear();
                scopeChain.Clear();
                imported.Clear();
                importedTypes.Clear();
                _IndentCount = 0;

                PushScope(globalScope);
                PushScope();
            }

            public Scope PeekScope()
            {
                if (scopeChain.Count == 0)
                    return null;

                return scopeChain.Last();
            }

            public void PushScope()
            {
                scopeChain.Add(new Scope(PeekScope()));
            }

            public void PushScope(Scope s)
            {
                s.Prev = PeekScope();

                scopeChain.Add(s);
            }

            public void PopScope()
            {
                if (ScopePopped != null)
                    ScopePopped(scopeChain.Last());

                scopeChain.RemoveAt(scopeChain.Count - 1);
            }

            public Scope ScopeAt(int index)
            {
                return scopeChain[index];
            }

            public bool ContainsIdInScopeChain(string name)
            {
                for (int i = scopeChain.Count - 1; i >= 0; i--)
                {
                    Scope s = scopeChain[i];
                    if (s.ContainsKey(name))
                        return true;
                }

                return false;
            }

            public Scope RetrieveScopeWhichContains(string name)
            {
                for (int i = scopeChain.Count - 1; i >= 0; i--)
                {
                    Scope s = scopeChain[i];
                    if (s.ContainsKey(name))
                        return s;
                }

                return null;
            }

            public SExp RetrieveFromScopeChain(string name)
            {
                for (int i = scopeChain.Count - 1; i >= 0; i--)
                {
                    Scope s = scopeChain[i];
                    if (s.ContainsKey(name))
                        return s[name];
                }

                return null;
            }

            public bool TryRemoveFromScopeChain(string name)
            {
                for (int i = scopeChain.Count - 1; i >= 0; i--)
                {
                    Scope s = scopeChain[i];
                    if (s.ContainsKey(name))
                    {
                        s.Remove(name);

                        return true;
                    }
                }

                return false;
            }

            public ListEnumerator PeekParameterEnumerator()
            {
                Assert(currentEvaluating.Peek() is SExpList, "SExpList expected");
                SExpList l = (SExpList)currentEvaluating.Peek();
                ListEnumerator result = new ListEnumerator(l);
                bool b = result.MoveNext();
                Assert(b, "Invalid function list");

                return result;
            }

            public List<object> RetrieveParameters(ListEnumerator parit)
            {
                List<object> result = null;
                SExp s = parit.TryPopParameter(this);
                while (s != null)
                {
                    if (result == null) result = new List<object>();

                    result.Add(s);
                    s = parit.TryPopParameter(this);
                }

                return result;
            }

            public List<object> RetrieveParameters(IEnumerable<SExp> parit)
            {
                List<object> result = null;
                IEnumerator<SExp> it = parit.GetEnumerator();
                while (it.MoveNext())
                {
                    if (result == null) result = new List<object>();

                    result.Add(it.Current.ToObject());
                }

                return result;
            }

            public List<object> RetrieveParameters(ParameterInfo[] parinfos, ListEnumerator parit)
            {
                List<object> result = null;
                foreach (ParameterInfo parInfo in parinfos)
                {
                    if (result == null) result = new List<object>();

                    result.Add(RetrieveParameter(parInfo, parit.PopParameter(this)));
                }

                return result;
            }

            public List<object> RetrieveParameters(ParameterInfo[] parinfos, IEnumerable<SExp> parit)
            {
                List<object> result = null;
                IEnumerator<SExp> it = parit.GetEnumerator();
                foreach (ParameterInfo parInfo in parinfos)
                {
                    if (result == null) result = new List<object>();

                    it.MoveNext();
                    result.Add(RetrieveParameter(parInfo, it.Current));
                }

                return result;
            }

            public object RetrieveParameter(ParameterInfo parinfo, SExp s)
            {
                if (parinfo.ParameterType == typeof(decimal))
                    return (decimal)s.ToNumber();
                else if (parinfo.ParameterType == typeof(double))
                    return (double)s.ToNumber();
                else if (parinfo.ParameterType == typeof(float))
                    return (float)s.ToNumber();
                else if (parinfo.ParameterType == typeof(long))
                    return (long)s.ToNumber();
                else if (parinfo.ParameterType == typeof(int))
                    return (int)s.ToNumber();
                else if (parinfo.ParameterType == typeof(char))
                    return (char)s.ToNumber();
                else if (parinfo.ParameterType == typeof(byte))
                    return (byte)s.ToNumber();
                else if (parinfo.ParameterType == typeof(bool))
                    return s.ToNumber() != default(number);
                else if (parinfo.ParameterType == typeof(string))
                    return s.ToText();
                else if (parinfo.ParameterType == typeof(object))
                    return s.ToObject();
                else
                    throw new TypeNotMatchedException("Host function invoking parameter type not matched");
            }

            public SExp Convert(object obj)
            {
                if (obj == null)
				{
                    return new SExpNil(this);
				}
                else if (obj.GetType() == typeof(decimal))
				{
                    return new SExpNumber(this, (number)(decimal)obj);
				}
                else if (obj.GetType() == typeof(double))
				{
                    return new SExpNumber(this, (number)(double)obj);
				}
                else if (obj.GetType() == typeof(float))
				{
                    return new SExpNumber(this, (number)(float)obj);
				}
                else if (obj.GetType() == typeof(long))
				{
                    return new SExpNumber(this, (number)(long)obj);
				}
                else if (obj.GetType() == typeof(int))
				{
                    return new SExpNumber(this, (number)(int)obj);
				}
                else if (obj.GetType() == typeof(char))
				{
                    return new SExpNumber(this, (number)(char)obj);
				}
                else if (obj.GetType() == typeof(byte))
				{
                    return new SExpNumber(this, (number)(byte)obj);
				}
                else if (obj.GetType() == typeof(bool))
				{
                    return new SExpNumber(this, (bool)obj ? 1 : 0);
				}
                else if (obj.GetType() == typeof(string))
				{
                    return new SExpString(this, (string)obj);
				}
                else if (obj is SExp)
				{
                    return (SExp)obj;
				}
                else if (obj is Array)
                {
                    SExpList r = new SExpList(this);
                    Array arr = (Array)obj;
                    foreach (var e in arr)
                        r.Add(Convert(e));

                    return r;
                }
                else
				{
                    return new SExpUserType(this, obj);
				}
            }
        }

        public class Scope : Dictionary<string, SExp>
        {
            public Scope Prev { get; set; }

            public bool InLambda { get; set; }

            public Scope(Scope prev)
            {
                Prev = prev;

                InLambda = false;
            }
        }

        public class ScopeChain : List<Scope>
        {
        }

        public class ScopeRaii : IDisposable
        {
            private RuntimeContext context = null;

            public ScopeRaii(RuntimeContext ctx)
            {
                context = ctx;
                context.PushScope();
            }

            public ScopeRaii(RuntimeContext ctx, Scope s)
            {
                context = ctx;
                context.PushScope(s);
            }

            public void Dispose()
            {
                context.PopScope();
            }
        }

        public class SendingRaii : IDisposable
        {
            private RuntimeContext context = null;

            private bool sending = false;

            public SendingRaii(RuntimeContext ctx)
            {
                context = ctx;
                sending = ctx.Sending;
                ctx.Sending = true;
            }

            public void Dispose()
            {
                context.Sending = sending;
            }
        }

        #endregion

        #region Helpers

        public class Counter
        {
            private int count;

            public int Count
            {
                get { return count; }
            }

            public void Inc()
            {
                count++;
            }
        }

        public class ListEnumerator : IEnumerator<SExp>
        {
            private IEnumerator<SExp> inter = null;

            public ListEnumerator(SExpList list)
            {
                inter = list.GetEnumerator();
            }

            public SExp Current
            {
                get { return inter.Current; }
            }

            public void Dispose()
            {
                inter.Dispose();
            }

            object IEnumerator.Current
            {
                get { return inter.Current; }
            }

            public bool MoveNext()
            {
                return inter.MoveNext();
            }

            public void Reset()
            {
                inter.Reset();
            }

            public SExp PopParameter(RuntimeContext context)
            {
                int row = Current.Row;
                int col = Current.Col;
                if (!MoveNext())
                    throw new TooFewParametersException("Too few parameters", row, col);

                context.CurrentEnumerator.Peek().Inc();

                if (context.Sending)
                    return Current;

                return Current.Eval();
            }

            public SExp PopLiteral(RuntimeContext context)
            {
                int row = Current.Row;
                int col = Current.Col;
                if (!MoveNext())
                    throw new TooFewParametersException("Too few parameters", row, col);

                context.CurrentEnumerator.Peek().Inc();

                return Current;
            }

            public SExp TryPopParameter(RuntimeContext context)
            {
                if (!MoveNext())
                    return null;

                context.CurrentEnumerator.Peek().Inc();

                return Current.Eval();
            }

            public SExp TryPopLiteral(RuntimeContext context)
            {
                if (!MoveNext())
                    return null;

                context.CurrentEnumerator.Peek().Inc();

                return Current;
            }
        }

        public static void Assert(bool cond, string msg)
        {
            Debug.Assert(cond, msg);
        }

        public static bool TryParse(string str, out number n)
        {
#if PocketPC
            try
            {
                n = number.Parse(str);

                return true;
            }
            catch
            {
                n = default(number);

                return false;
            }
#else
            return number.TryParse(str, out n);
#endif
        }

        #endregion

        #region Fields

        private bool byref = false;

        private ParsingContext parsingContext = null;

        private RuntimeContext runtimeContext = null;

        private SExpList root = null;

        #endregion

        #region Properties

        public RuntimeContext Context { get { return runtimeContext; } }

        public SExpList Root { get { return root; } set { root = value; } }

        public PrinterFunc Printer { get; set; }

        #endregion

        #region Methods

        #region Constructor

        public LSharp()
        {
            runtimeContext = new RuntimeContext(this);
            root = new SExpList(runtimeContext);
            runtimeContext.CurrentEvaluating.Push(root);
            Printer = Console.Out.Write;

            CoreLib.Open(this);
            CalcLib.Open(this);
            StandardLib.Open(this);
        }

        public LSharp(LSharp refed)
        {
            runtimeContext = refed.Context;
            root = new SExpList(runtimeContext);
            byref = true;
            Printer = Console.Out.Write;
        }

        ~LSharp()
        {
            if (!byref)
            {
                StandardLib.Close(this);
                CalcLib.Close(this);
                CoreLib.Close(this);
            }
        }

        #endregion

        #region Registering/unregistering

        public void Register(HostFuncDelegate dele)
        {
            Register(dele.Method, dele.Target, null, true);
        }

        public void Register(HostFuncDelegate dele, string alias)
        {
            Register(dele.Method, dele.Target, alias, true);
        }

        public void Register(Delegate dele)
        {
            Register(dele.Method, dele.Target, null, false);
        }

        public void Register(Delegate dele, string alias)
        {
            Register(dele.Method, dele.Target, alias, false);
        }

        public void Register(Type t, object target, string methodName)
        {
            Register(t.GetMethod(methodName), target, null, false);
        }

        public void Register(Type t, object target, string methodName, string alias)
        {
            Register(t.GetMethod(methodName), target, alias, false);
        }

        public void Register(Type t, string methodName)
        {
            Register(t, null, methodName);
        }

        public void Register(Type t, string methodName, string alias)
        {
            Register(t, null, methodName, alias);
        }

        public void Register(MethodInfo method, object self, string alias, bool normalized)
        {
            runtimeContext.HostFuncs[alias ?? method.Name] = new HostFunc(method, self, runtimeContext, normalized);
        }

        public void Register(object obj, string alias)
        {
            Register(runtimeContext.Convert(obj), alias);
        }

        public void Register(SExp s, string alias)
        {
            runtimeContext.GlobalScope[alias] = s;
        }

        public void Unregister(string name)
        {
            if (runtimeContext.HostFuncs.ContainsKey(name))
                runtimeContext.HostFuncs.Remove(name);
            else if (runtimeContext.LastScope.ContainsKey(name))
                runtimeContext.LastScope.Remove(name);
        }

        #endregion

        #region Loading/parsing

        public void ClearExecutable()
        {
            root.Clear();
        }

        public void ClearRuntime()
        {
            runtimeContext.Clear();
        }

        public void Clear()
        {
            ClearExecutable();
            ClearRuntime();
        }

        public void LoadString(string txt)
        {
            ClearExecutable();

            parsingContext = new ParsingContext();
            parsingContext.Push(root, ParsingState.Common);

            int bkt = 0;
            int row = 1;
            int col = 0;
            try
            {
                char wrapped = '\0';
                for (int i = 0; i < txt.Length; i++)
                {
                    char c = txt[i];

                    if ((c == '\n' || c == '\r') && (wrapped == '\0' || wrapped == c))
                    {
                        wrapped = c;
                        ++row;
                        col = 1;
                    }
                    else
                    {
                        wrapped = '\0';
                        ++col;
                    }

                    switch (parsingContext.State)
                    {
                        case ParsingState.Common:
                            if (c == '#')
                            {
                                parsingContext.Push();
                            }
                            else if (c == '"')
                            {
                                SExpString s = new SExpString(runtimeContext);
                                s.Row = row; s.Col = col;
                                parsingContext.Push(s, ParsingState.String0);
                            }
                            else if (c == '\'')
                            {
                                SExpString s = new SExpString(runtimeContext);
                                s.Row = row; s.Col = col;
                                parsingContext.Push(s, ParsingState.String1);
                            }
                            else if (c == '(')
                            {
                                bkt++;
                                SExpList s = new SExpList(runtimeContext);
                                s.Row = row; s.Col = col;
                                parsingContext.Push(s, ParsingState.Common);
                            }
                            else if (c == ')')
                            {
                                bkt--;
                                if (bkt < 0)
                                    throw new BracketNotMatchedException("Too many close brackets", row, col);
                                SExp s = parsingContext.Cut(runtimeContext);
                                if (s != null)
                                {
                                    s.Row = row; s.Col = col;
                                    parsingContext.Push(s, ParsingState.Common);
                                    parsingContext.Pop();
                                }

                                parsingContext.Pop();
                            }
                            else if (c == '&')
                            {
                                parsingContext.Quot = true;
                            }
                            else if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                            {
                                SExp s = parsingContext.Cut(runtimeContext);
                                if (s != null)
                                {
                                    s.Row = row; s.Col = col;
                                    parsingContext.Push(s, ParsingState.Common);
                                    parsingContext.Pop();
                                }
                            }
                            else
                            {
                                parsingContext.AppendChar(c);
                            }
                            break;
                        case ParsingState.String0:
                            if (c == '"')
                                parsingContext.Pop();
                            else
                                parsingContext.AppendChar(c);
                            break;
                        case ParsingState.String1:
                            if (c == '\'')
                                parsingContext.Pop();
                            else
                                parsingContext.AppendChar(c);
                            break;
                        case ParsingState.Comment:
                            if (c == '\r' || c == '\n')
                                parsingContext.Pop();
                            break;
                    }
                }

                if (bkt != 0)
                    throw new BracketNotMatchedException("Close brackets missing", row, col);
            }
            catch (LSharpException ex)
            {
                if (ex.Row == -1 && ex.Col == -1)
                {
                    ex.Row = row;
                    ex.Col = col;
                }
                throw ex;
            }
            catch (Exception ex)
            {
                throw new SystemException(ex.Message, row, col);
            }

            parsingContext = null;
        }

        public void LoadFile(string fileName)
        {
			using (FileStream fs = new FileStream(fileName, FileMode.Open))
            {
                using (StreamReader sr = new StreamReader(fs))
                {
                    LoadString(sr.ReadToEnd());
                }
            }
        }

        #endregion

        #region Runtime

        public SExp Execute()
        {
            return root.Eval();
        }

        #endregion

        #endregion

        #region S-Expressions

        public interface IQuotable
        {
            bool Quot { get; set; }
        }

        public abstract class SExp : ICloneable
        {
            public enum Types
            {
                Nil,
                Number,
                String,
                List,
                Dict,
                HostFunc,
                InterpretedFunc,
                Lambda,
                Id,
                UserType,
                UserEnum
            }

            protected RuntimeContext context = null;

            public abstract Types Type { get; }

            public abstract string TypeString { get; }

            public int Row { get; set; }
            public int Col { get; set; }

            public SExp(RuntimeContext ctx)
            {
                Row = Col = -1;

                context = ctx;
            }

            public T As<T>() where T : SExp
            {
                return (T)this;
            }

            public virtual void Take(ref string buffer)
            {
                buffer = string.Empty;
            }

            public virtual void Foreach(Action<SExp> act)
            {
                // Do nothing
            }

            public virtual SExp Eval()
            {
                return this;
            }

            public virtual SExp Recv(params SExp[] msg)
            {
                return this;
            }

            public virtual object ToObject()
            {
                return Eval().ToObject();
            }

            public virtual number ToNumber()
            {
                return Eval().ToNumber();
            }

            public virtual string ToText()
            {
                return Eval().ToText();
            }

            public virtual bool ToBool()
            {
                return Eval().ToBool();
            }

            public abstract object Clone();

            protected string IndentSpace()
            {
                if (!context._Indent)
                    return string.Empty;

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < context._IndentCount; i++)
                    sb.Append("  ");

                return sb.ToString();
            }

            public string ToString(bool indent)
            {
                context._IndentCount = 0;
                context._Indent = indent;

                return ToString();
            }
        }

        public class SExpNil : SExp
        {
            public override Types Type { get { return Types.Nil; } }

            public override string TypeString { get { return "nil"; } }

            public SExpNil(RuntimeContext ctx)
                : base(ctx)
            {
            }

            public override SExp Eval()
            {
                return this;
            }

            public override object ToObject()
            {
                return null;
            }

            public override number ToNumber()
            {
                return default(number);
            }

            public override string ToText()
            {
                return "nil";
            }

            public override bool ToBool()
            {
                return false;
            }

            public override object Clone()
            {
                return new SExpNil(context);
            }

            public override string ToString()
            {
                return IndentSpace() + "nil" + (context._Indent ? Environment.NewLine : string.Empty);
            }
        }

        public class SExpNumber : SExp
        {
            private number data = default(number);

            public override Types Type { get { return Types.Number; } }

            public override string TypeString { get { return "number"; } }

            public SExpNumber(RuntimeContext ctx)
                : base(ctx)
            {
            }

            public SExpNumber(RuntimeContext ctx, number d)
                : base(ctx)
            {
                data = d;
            }

            public override void Take(ref string buffer)
            {
                if (!TryParse(buffer, out data))
                {
                    data = default(number);

                    throw new InvalidDataException("Invalid number format", -1, -1);
                }

                base.Take(ref buffer);
            }

            public override SExp Eval()
            {
                return this;
            }

            public override object ToObject()
            {
                return ToNumber();
            }

            public override number ToNumber()
            {
                return data;
            }

            public override string ToText()
            {
                return data.ToString();
            }

            public override bool ToBool()
            {
                return ToNumber() != default(number);
            }

            public override object Clone()
            {
                return new SExpNumber(context, data);
            }

            public override string ToString()
            {
                return IndentSpace() + data.ToString() + (context._Indent ? Environment.NewLine : string.Empty);
            }
        }

        public class SExpString : SExp
        {
            private string data = null;

            public override Types Type { get { return Types.String; } }

            public override string TypeString { get { return "string"; } }

            public SExpString(RuntimeContext ctx)
                : base(ctx)
            {
            }

            public SExpString(RuntimeContext ctx, string d)
                : base(ctx)
            {
                data = d;
            }

            public override void Take(ref string buffer)
            {
                data = buffer;

                base.Take(ref buffer);
            }

            public override SExp Eval()
            {
                return this;
            }

            public override object ToObject()
            {
                return ToText();
            }

            public override number ToNumber()
            {
                return number.Parse(data);
            }

            public override string ToText()
            {
                return data;
            }

            public override bool ToBool()
            {
                return true;
            }

            public override object Clone()
            {
                return new SExpString(context, data);
            }

            public override string ToString()
            {
                return IndentSpace() + "\"" + data + "\"" + (context._Indent ? Environment.NewLine : string.Empty);
            }
        }

        public class SExpList : SExp, IQuotable, IList<SExp>
        {
            private List<SExp> inter = new List<SExp>();

            public bool Quot { get; set; }

            public override Types Type { get { return Types.List; } }

            public override string TypeString { get { return "list"; } }

            public SExpList(RuntimeContext ctx)
                : base(ctx)
            {
            }

            public override void Foreach(Action<SExp> act)
            {
                foreach (SExp s in this)
                    act(s);
            }

            public override SExp Eval()
            {
                if (Quot)
                    return this;

                return _Eval();
            }

            public override SExp Recv(params SExp[] msg)
            {
                SExp m0 = msg.First();
                if (m0 is SExpNumber)
                {
                    int i = (int)m0.ToNumber();
                    if (i < 0)
                        i += Count;

                    return this[i];
                }
                else if (m0 is SExpHostFunc && m0.As<SExpHostFunc>().Data == "eval")
                {
                    return _Eval();
                }
                else if (m0 is SExpHostFunc && m0.As<SExpHostFunc>().Data == "+")
                {
                    int index = Count;
                    SExp me = null;
                    if (msg.Length == 3 && msg[2] != null)
                    {
                        index = (int)msg[1].ToNumber();
                        me = msg[2];
                    }
                    else if ((msg.Length == 2) || (msg.Length == 3 && msg[2] == null))
                    {
                        me = msg[1];
                    }
                    Insert(index, me);

                    return me;
                }
                else if (m0 is SExpHostFunc && m0.As<SExpHostFunc>().Data == "-")
                {
                    SExp m1 = msg[1];
                    SExp ret = this[(int)m1.ToNumber()];
                    RemoveAt((int)m1.ToNumber());

                    return ret;
                }
                else
                {
                    throw new TypeNotMatchedException("Type not matched", Row, Col);
                }
            }

            public override string ToText()
            {
                if (Quot)
                    return ToString(false);

                return ToString(false);
            }

            public override object Clone()
            {
                SExpList result = new SExpList(context);
                foreach (SExp s in this)
                    result.Add((SExp)s.Clone());

                return result;
            }

            public override string ToString()
            {
                return ToString(true);
            }

            private new string ToString(bool withQuotMark)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(IndentSpace());
                if (Quot && withQuotMark)
                    sb.Append("&");
                sb.Append("(" + (context._Indent ? Environment.NewLine : string.Empty));
                context._IndentCount++;
                for (int i = 0; i < Count; i++)
                {
                    SExp s = this[i];
                    sb.Append(s.ToString());
                    if (i != Count - 1 && !context._Indent)
                        sb.Append(" ");
                }
                context._IndentCount--;
                sb.Append(IndentSpace());
                sb.Append(")" + (context._Indent ? Environment.NewLine : string.Empty));

                return sb.ToString();
            }

            private SExp _Eval()
            {
                if (this != context.Interpreter.Root)
                    context.CurrentEvaluating.Push(this);

                SExp result = null;

                Counter i = new Counter();
                context.CurrentEnumerator.Push(i);
                for (; i.Count < Count; i.Inc())
                {
                    SExp en = this[i.Count];
                    result = en.Eval();
                }

                context.CurrentEnumerator.Pop();

                if (this != context.Interpreter.Root)
                {
                    SExp l = context.CurrentEvaluating.Pop();
                    Assert(l == this, "Unmatched list push/pop");
                }

                return result;
            }

            #region Interface methods

            public int IndexOf(SExp item)
            {
                return inter.IndexOf(item);
            }

            public void Insert(int index, SExp item)
            {
                inter.Insert(index, item);
            }

            public void RemoveAt(int index)
            {
                inter.RemoveAt(index);
            }

            public SExp this[int index]
            {
                get { return inter[index]; }
                set { inter[index] = value; }
            }

            public void Add(SExp item)
            {
                inter.Add(item);
            }

            public void Clear()
            {
                inter.Clear();
            }

            public bool Contains(SExp item)
            {
                return inter.Contains(item);
            }

            public void CopyTo(SExp[] array, int arrayIndex)
            {
                inter.CopyTo(array, arrayIndex);
            }

            public int Count
            {
                get { return inter.Count; }
            }

            public bool IsReadOnly
            {
                get { return false; }
            }

            public bool Remove(SExp item)
            {
                return inter.Remove(item);
            }

            public IEnumerator<SExp> GetEnumerator()
            {
                return inter.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return inter.GetEnumerator();
            }

            #endregion
        }

        // Can be created during runtime only
        public class SExpDict : SExp, IDictionary<string, SExp>, ICollection<KeyValuePair<string, SExp>>, IEnumerable<KeyValuePair<string, SExp>>
        {
            private Dictionary<string, SExp> inter = null;

            public override SExp.Types Type { get { return Types.Dict; } }

            public override string TypeString { get { return "dict"; } }

            public SExpDict(RuntimeContext ctx)
                : base(ctx)
            {
                inter = new Dictionary<string, SExp>();
            }

            public override SExp Eval()
            {
                return this;
            }

            public override SExp Recv(params SExp[] msg)
            {
                SExp m0 = msg.First();
                if ((m0 is SExpId) || (m0 is SExpString))
                {
                    if (m0 is SExpId && ContainsKey(m0.As<SExpId>().Name))
                        return this[m0.As<SExpId>().Name];
                    else if (ContainsKey(m0.ToText()))
                        return this[m0.ToText()];
                    else
                        return null;
                }
                else if (m0 is SExpHostFunc && m0.As<SExpHostFunc>().Data == "+")
                {
                    SExp m1 = msg[1];
                    SExp m2 = msg[2];
                    this[m1.ToText()] = m2;

                    return m2;
                }
                else if (m0 is SExpHostFunc && m0.As<SExpHostFunc>().Data == "-")
                {
                    SExp m1 = msg[1];
                    SExp ret = this[m1.ToText()];
                    Remove(m1.ToText());

                    return ret;
                }
                else
                {
                    throw new TypeNotMatchedException("Type not matched", Row, Col);
                }
            }

            public override object Clone()
            {
                throw new NotImplementedException();
            }

            #region Interface methods

            public void Add(string key, SExp value)
            {
                inter.Add(key, value);
            }

            public bool ContainsKey(string key)
            {
                return inter.ContainsKey(key);
            }

            public ICollection<string> Keys
            {
                get { return inter.Keys; }
            }

            public bool Remove(string key)
            {
                return inter.Remove(key);
            }

            public bool TryGetValue(string key, out SExp value)
            {
                return inter.TryGetValue(key, out value);
            }

            public ICollection<SExp> Values
            {
                get { return inter.Values; }
            }

            public SExp this[string key]
            {
                get { return inter[key]; }
                set { inter[key] = value; }
            }

            public void Add(KeyValuePair<string, SExp> item)
            {
                inter.Add(item.Key, item.Value);
            }

            public void Clear()
            {
                inter.Clear();
            }

            public bool Contains(KeyValuePair<string, SExp> item)
            {
                return inter.Contains(item);
            }

            public void CopyTo(KeyValuePair<string, SExp>[] array, int arrayIndex)
            {
                throw new NotImplementedException();
            }

            public int Count
            {
                get { return inter.Count; }
            }

            public bool IsReadOnly
            {
                get { return false; }
            }

            public bool Remove(KeyValuePair<string, SExp> item)
            {
                if (inter.ContainsKey(item.Key) && inter[item.Key] == item.Value)
                    return inter.Remove(item.Key);
                else
                    return false;
            }

            public IEnumerator<KeyValuePair<string, SExp>> GetEnumerator()
            {
                return inter.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return inter.GetEnumerator();
            }

            #endregion
        }

        public class SExpHostFunc : SExp
        {
            public override Types Type { get { return Types.HostFunc; } }

            public override string TypeString { get { return "nfunc"; } }

            public string Data { get; private set; }

            public SExpHostFunc(RuntimeContext ctx)
                : base(ctx)
            {
                Data = null;
            }

            public override void Take(ref string buffer)
            {
                Data = buffer;

                base.Take(ref buffer);
            }

            public override SExp Eval()
            {
                SExp result = null;

                Assert(context.CurrentEvaluating.Peek() is SExpList, "Invalid parent type");

                if (!context.HostFuncs.ContainsKey(Data))
                    throw new FuncNotImplementedException("Function \"" + Data + "\" not implemented", Row, Col);

                try
                {
                    HostFunc func = context.HostFuncs[Data];
                    object ret = null;
                    if (func.Normalized)
                    {
                        ret = func.Invoke();
                    }
                    else
                    {
                        LSharp.ListEnumerator en = context.PeekParameterEnumerator();
                        ParameterInfo[] parInfos = func.MethodInfo.GetParameters();

                        List<object> pars = context.RetrieveParameters(parInfos, en);

                        ret = func.Invoke(pars == null ? null : pars.ToArray());
                    }

                    result = context.Convert(ret);
                }
                catch (LSharpException ex)
                {
                    if (ex.Row == -1 && ex.Col == -1)
                    {
                        ex.Row = Row;
                        ex.Col = Col;
                    }
                    throw ex;
                }
                catch (Exception ex)
                {
                    throw new SystemException(ex.Message, Row, Col);
                }

                return result;
            }

            public override object Clone()
            {
                SExpHostFunc result = new SExpHostFunc(context);
                result.Data = Data;

                return result;
            }

            public override string ToString()
            {
                return IndentSpace() + Data + (context._Indent ? Environment.NewLine : string.Empty);
            }
        }

		// Can be created during runtime only
		public class SExpInterpretedFunc : SExp
        {
            private SExpList args = null;
            private SExpList body = null;

            public override Types Type { get { return Types.InterpretedFunc; } }

            public override string TypeString { get { return "func"; } }

            public string Name { get; set; }

            public SExpInterpretedFunc(RuntimeContext ctx, SExpList _args, SExpList _body)
                : base(ctx)
            {
                args = _args;
                body = _body;
            }

            public override SExp Eval()
            {
                SExp result = null;

                Assert(context.CurrentEvaluating.Peek() is SExpList, "Invalid parent type");

                using (ScopeRaii sr = new LSharp.ScopeRaii(context))
                {
                    ListEnumerator en = context.PeekParameterEnumerator();
                    foreach (SExp arg in args)
                    {
                        SExp a = en.PopParameter(context);
                        context.LastScope[arg.As<SExpId>().Name] = a;
                    }
                    result = body.Eval();
                }

                return result;
            }

            public SExp Eval(params object[] pars)
            {
                SExp result = null;

                Assert(context.CurrentEvaluating.Peek() is SExpList, "Invalid parent type");

                using (ScopeRaii sr = new LSharp.ScopeRaii(context))
                {
                    int i = 0;
                    foreach (SExp arg in args)
                        context.LastScope[arg.As<SExpId>().Name] = context.Convert(pars[i++]);
                    result = body.Eval();
                }

                return result;
            }

            public override object Clone()
            {
                return new SExpInterpretedFunc(context, (SExpList)args.Clone(), (SExpList)body.Clone());
            }

            public string Format()
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("def ");
                sb.Append(Name);
                sb.Append(" ");
                sb.Append(args.ToString(false));
                sb.Append(" ");
                sb.Append(body.ToString(false));

                return sb.ToString();
            }

            public string Format(string prefixPerLine)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("def ");
                sb.Append(Name);
                sb.Append(" ");
                sb.Append(args.ToString(false));
                sb.Append(Environment.NewLine);
                sb.Append(prefixPerLine);
                sb.Append("(");
                sb.Append(Environment.NewLine);
                foreach (LSharp.SExp s in body)
                {
                    sb.Append("\t");
                    sb.Append(prefixPerLine);
                    sb.Append(s.ToString(false));
                    sb.Append(Environment.NewLine);
                }
                sb.Append(prefixPerLine);
                sb.Append(")");
                sb.Append(Environment.NewLine);

                return sb.ToString();
            }

            public override string ToString()
            {
                return IndentSpace() + Name + (context._Indent ? Environment.NewLine : string.Empty);
            }
        }

		// Can be created during runtime only
		public class SExpLambda : SExp
        {
            private SExpList args = null;
            private SExpList body = null;

            public override Types Type { get { return Types.Lambda; } }

            public override string TypeString { get { return "lambda"; } }

            public Scope Scope { get; private set; }

            public Dictionary<LSharp.SExpId, Scope> Upvalues { get; private set; }

            public SExpLambda(RuntimeContext ctx)
                : base(ctx)
            {
                Scope = new Scope(ctx.LastScope);
                Scope.InLambda = true;

                Upvalues = new Dictionary<SExpId, Scope>();
            }

            public void Init(SExpList _args, SExpList _body)
            {
                args = _args;
                body = _body;
            }

            public override SExp Eval()
            {
                SExp result = null;

                Assert(context.CurrentEvaluating.Peek() is SExpList, "Invalid parent type");

                using (ScopeRaii sr = new LSharp.ScopeRaii(context, Scope))
                {
                    ListEnumerator en = context.PeekParameterEnumerator();
                    foreach (SExp arg in args)
                    {
                        SExp a = en.PopParameter(context);
                        context.LastScope[arg.As<SExpId>().Name] = a;
                    }
                    result = body.Eval();
                }

                return result;
            }

            public override SExp Recv(params SExp[] msg)
            {
                SExp m0 = msg.First();
                if (!(m0 is SExpId))
                    throw new IdExpectedException("ID expected");

                if (Scope.ContainsKey(m0.As<SExpId>().Name))
                {
                    SExp s = Scope[m0.As<SExpId>().Name];

                    SExp result = null;
                    using (ScopeRaii sr = new LSharp.ScopeRaii(context, Scope))
                    {
                        result = s.Eval();
                    }

                    return result;
                }

                return null;
            }

            public override object Clone()
            {
                throw new LambdaUsageException("Lambda cannot be cloned, nested", Row, Col);
            }

            public override string ToString()
            {
                return IndentSpace() + "lambda" + (context._Indent ? Environment.NewLine : string.Empty);
            }
        }

        public class SExpId : SExp, IQuotable
        {
            public override Types Type { get { return Types.Id; } }

            public override string TypeString { get { return "id"; } }

            public bool Quot { get; set; }

            public string Name { get; private set; }

            public SExpId(RuntimeContext ctx, bool _quot)
                : base(ctx)
            {
                Quot = _quot;
            }

            public override void Take(ref string buffer)
            {
                Name = buffer;

                base.Take(ref buffer);
            }

            public override SExp Eval()
            {
                if (!context.ContainsIdInScopeChain(Name))
                    return this;

                SExp s = context.RetrieveFromScopeChain(Name);

                if (Quot)
                    return s;

                return s.Eval();
            }

            public override object Clone()
            {
                return new SExpId(context, Quot);
            }

            public override string ToString()
            {
                return IndentSpace() + Name + (context._Indent ? Environment.NewLine : string.Empty);
            }
        }

		// Can be created during runtime only
		public class SExpUserType : SExp
        {
            public override SExp.Types Type { get { return Types.UserType; } }

            public override string TypeString { get { return "user_type"; } }

            public Type StaticType { get; private set; }

            public object Data { get; private set; }

            public SExpUserType(RuntimeContext ctx, object target)
                : base(ctx)
            {
                Data = target;
                StaticType = null;
            }

            public SExpUserType(RuntimeContext ctx, Type staticType)
                : base(ctx)
            {
                Data = null;
                StaticType = staticType;
            }

            public override SExp Recv(params SExp[] msg)
            {
                if ((msg.Length == 0) || (!(msg[0] is SExpId) && !(msg[0] is SExpString)))
                    throw new IdOrStringExpectedException("ID or string expected", Row, Col);
                string memberName = null;
                if (msg[0] is SExpId) memberName = msg[0].As<SExpId>().Name;
                else if (msg[0] is SExpString) memberName = msg[0].ToText();

                List<object> args = context.RetrieveParameters(msg.Skip(1));
                IEnumerable<Type> types =
                    args == null ?
                    new Type[] { } :
                    from arg in args select arg.GetType();

                Type type = null;
                if (Data != null) type = Data.GetType();
                else if (StaticType != null) type = StaticType;
                else throw new TypeNotFoundException("User type not found", Row, Col);
                MethodInfo methodInfo = type.GetMethod(memberName, types.ToArray());
                if (methodInfo == null)
                {
                    methodInfo = type.GetMethod(memberName);
                    if (methodInfo != null)
                    {
                        ParameterInfo[] _pis = methodInfo.GetParameters();
                        if (_pis.Length != args.Count)
                        {
                            methodInfo = null;
                        }
                        else
                        {
                            for (int i = 0; i < _pis.Length; i++)
                            {
                                ParameterInfo _pi = _pis[i];
                                args[i] = Convert.ChangeType(args[i], _pi.ParameterType, null);
                            }
                        }
                    }
                }
                PropertyInfo prpertyInfo = type.GetProperty(memberName);

                if (methodInfo != null)
                {
                    object ret = methodInfo.Invoke(Data, args == null ? null : args.ToArray());

                    return context.Convert(ret);
                }
                else if (prpertyInfo != null)
                {
                    if (args == null)
                    {
                        object ret = prpertyInfo.GetValue(Data, null);

                        return context.Convert(ret);
                    }
                    else
                    {
                        if (args.Count != 1)
                            throw new TooManyParametersException("Too many parameters", Row, Col);

                        object arg = Convert.ChangeType(args.First(), prpertyInfo.PropertyType, null);
                        prpertyInfo.SetValue(Data, arg, null);
                        object ret = prpertyInfo.GetValue(Data, null);

                        return context.Convert(ret);
                    }
                }
                else
                {
                    throw new OperationNotFoundException("Operation not found: " + memberName, Row, Col);
                }
            }

            public override object ToObject()
            {
                return Data;
            }

            public override string ToText()
            {
                return Data.ToString();
            }

            public override object Clone()
            {
                throw new NotImplementedException();
            }
        }

		// Can be created during runtime only
		public class SExpUserEnum : SExp
        {
            public override Types Type { get { return Types.UserEnum; } }

            public override string TypeString { get { return "use_enum"; } }

            public Enum Data { get; private set; }

            public SExpUserEnum(RuntimeContext ctx, Enum data)
                : base(ctx)
            {
                Data = data;
            }

            public override object ToObject()
            {
                return Data;
            }

            public override string ToText()
            {
                return Data.ToString();
            }

            public override object Clone()
            {
                throw new NotImplementedException();
            }
        }

        #endregion
    }

    #region Exceptions

    public class LSharpException : Exception
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public LSharpException(string msg, int r, int c)
            : base(msg)
        {
            Row = r;
            Col = c;
        }
    }

    public class SystemException : LSharpException
    {
        public SystemException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public SystemException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class BracketNotMatchedException : LSharpException
    {
        public BracketNotMatchedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public BracketNotMatchedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class InvalidDataException : LSharpException
    {
        public InvalidDataException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public InvalidDataException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class OperationNotFoundException : LSharpException
    {
        public OperationNotFoundException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public OperationNotFoundException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class TypeNotMatchedException : LSharpException
    {
        public TypeNotMatchedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public TypeNotMatchedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class FuncNotImplementedException : LSharpException
    {
        public FuncNotImplementedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public FuncNotImplementedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class LambdaUsageException : LSharpException
    {
        public LambdaUsageException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public LambdaUsageException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class TooFewParametersException : LSharpException
    {
        public TooFewParametersException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public TooFewParametersException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class TooManyParametersException : LSharpException
    {
        public TooManyParametersException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public TooManyParametersException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class IdExpectedException : LSharpException
    {
        public IdExpectedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public IdExpectedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class NumberExpectedException : LSharpException
    {
        public NumberExpectedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public NumberExpectedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class StringExpectedException : LSharpException
    {
        public StringExpectedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public StringExpectedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class IdOrStringExpectedException : LSharpException
    {
        public IdOrStringExpectedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public IdOrStringExpectedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class ListExpectedException : LSharpException
    {
        public ListExpectedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public ListExpectedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class CollectionExpectedException : LSharpException
    {
        public CollectionExpectedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public CollectionExpectedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class ParametersExpectedException : LSharpException
    {
        public ParametersExpectedException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public ParametersExpectedException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class FileNotFoundException : LSharpException
    {
        public FileNotFoundException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public FileNotFoundException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class TypeNotFoundException : LSharpException
    {
        public TypeNotFoundException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public TypeNotFoundException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    public class UnknownEscapeCharException : LSharpException
    {
        public UnknownEscapeCharException(string msg, int r, int c)
            : base(msg, r, c)
        {
        }

        public UnknownEscapeCharException(string msg)
            : base(msg, -1, -1)
        {
        }
    }

    #endregion

    #region Core libraries

    public static class CoreLib
    {
        public static object Import(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp f = en.PopParameter(context);
            if (!(f is LSharp.SExpString))
                throw new StringExpectedException("String expected");

            string file = f.ToText();

            if (!File.Exists(file))
            {
                if (File.Exists(file + ".ls"))
                    file += ".ls";
                else if (File.Exists(file + ".exe"))
                    file += ".exe";
                else if (File.Exists(file + ".dll"))
                    file += ".dll";
                else
                    throw new FileNotFoundException("File not found: " + file);
            }

            FileInfo fi = new FileInfo(file);
            if (context.Imported.Contains(fi.FullName))
                return true;
            else
                context.Imported.Add(fi.FullName);

            if (fi.Extension == ".exe" || fi.Extension == ".dll")
            {
                Assembly asm = Assembly.LoadFrom(file);
                foreach (Type t in asm.GetTypes())
                    context.ImportedTypes[t.FullName] = t;

                return asm;
            }

            LSharp nls = new LSharp(context.Interpreter);
            nls.LoadFile(file);

            return nls.Execute();
        }

        public static object Exec(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp s = en.PopParameter(context);
            if (!(s is LSharp.SExpString))
                throw new StringExpectedException("String expected");

            LSharp nls = new LSharp(context.Interpreter);
            nls.LoadString(s.ToText());

            return nls.Execute();
        }

        public static object Is(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp d = en.PopParameter(context);
            LSharp.SExp s = en.PopParameter(context);
            if (!(s is LSharp.SExpString))
                throw new StringExpectedException("String expected");

            string ts = d.TypeString;

            return ts == s.ToText();
        }

        public static object New(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp t = en.PopParameter(context);
            if (!(t is LSharp.SExpString))
                throw new StringExpectedException("Type string expected");

            Func<string, Type> retrieveType = (_n) =>
            {
                Type result = Type.GetType(_n);
                if (result != null)
                    return result;

                if (context.ImportedTypes.ContainsKey(_n))
                    return context.ImportedTypes[_n];

                return null;
            };
            string typeName = t.ToText();
            Type type = retrieveType(typeName);
            if (type == null)
                throw new TypeNotFoundException("Type not found: " + typeName);

            List<object> args = null;
            LSharp.SExp e = en.TryPopParameter(context);
            while (e != null)
            {
                if (args == null)
                    args = new List<object>();
                args.Add(e.ToObject());
                e = en.TryPopParameter(context);
            }

            try
            {
#if PocketPC
                object obj = Activator.CreateInstance(type);
#else
                object obj = Activator.CreateInstance(type, args == null ? null : args.ToArray());
#endif

                return new LSharp.SExpUserType(context, obj);
            }
            catch (MissingMethodException)
            {
                return new LSharp.SExpUserType(context, type);
            }
        }

        public static object EnumInt(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp t = en.PopParameter(context);
            LSharp.SExp e = en.PopParameter(context);
            Type _t = Type.GetType(t.ToText());
            Enum _e = (Enum)Enum.Parse(_t, e.ToText(), false);

            return new LSharp.SExpUserEnum(context, _e);
        }

        public static object List(LSharp.RuntimeContext context)
        {
            LSharp.SExpList result = new LSharp.SExpList(context);

            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp e = en.TryPopParameter(context);
            while (e != null)
            {
                result.Add(e);
                e = en.TryPopParameter(context);
            }

            return result;
        }

        public static object Cons(LSharp.RuntimeContext context)
        {
            LSharp.SExpList result = new LSharp.SExpList(context);

            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp e = en.PopParameter(context);
            LSharp.SExp l = en.PopParameter(context);
            result.Add(e);
            if (l is LSharp.SExpList)
            {
                foreach (LSharp.SExp s in l.As<LSharp.SExpList>())
                    result.Add(s);
            }
            else
            {
                result.Add(l);
            }

            return result;
        }

        public static object Dict(LSharp.RuntimeContext context)
        {
            LSharp.SExpDict result = new LSharp.SExpDict(context);

            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp k = en.TryPopParameter(context);
            while (k != null)
            {
                LSharp.SExp v = en.PopParameter(context);
                result[k.ToText()] = v;
                k = en.TryPopParameter(context);
            }

            return result;
        }

        public static object Cond(LSharp.RuntimeContext context)
        {
            LSharp.SExp result = null;
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp c = en.TryPopParameter(context);
            LSharp.SExp v = null;
            bool walked = false;
            while (c != null)
            {
                v = en.PopLiteral(context);
                if (c.ToBool())
                {
                    result = v.Eval();
                    walked = true;

                    break;
                }
                if (c is LSharp.SExpNil)
                    break;
                c = en.TryPopParameter(context);
            }

            if (!walked)
            {
                if (v != null)
                    v.Eval();
            }
            while (en.TryPopLiteral(context) != null)
			{
                // Pop until empty
			}

            return result;
        }

        public static object If(LSharp.RuntimeContext context)
        {
            LSharp.SExp result = null;
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp c = en.PopParameter(context);
            if (c.ToBool())
            {
                LSharp.SExp v = en.PopLiteral(context);
                en.PopLiteral(context);
                result = v.Eval();
            }
            else
            {
                en.PopLiteral(context);
                LSharp.SExp v = en.PopLiteral(context);
                result = v.Eval();
            }

            return result;
        }

        public static object Car(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp l = en.PopParameter(context);

            if (!(l is LSharp.SExpList))
                return null;

            return l.As<LSharp.SExpList>().First();
        }

        public static object Cdr(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp l = en.PopParameter(context);

            if (!(l is LSharp.SExpList))
                return null;

            LSharp.SExpList result = new LSharp.SExpList(context);
            bool f = false;
            foreach (LSharp.SExp s in l.As<LSharp.SExpList>())
            {
                if (!f)
                {
                    f = true;

                    continue;
                }

                result.Add(s);
            }

            return result;
        }

        public static object Eval(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();

            return en.PopParameter(context).Eval();
        }

        public static object Var(LSharp.RuntimeContext context)
        {
            LSharp.SExp name = null;

            using (LSharp.ScopeRaii sr = new LSharp.ScopeRaii(context))
            {
                LSharp.ListEnumerator en = context.PeekParameterEnumerator();
                name = en.PopLiteral(context);
                LSharp.SExp init = en.TryPopParameter(context);
                if (!(name is LSharp.SExpId))
                    throw new IdExpectedException("ID expected");
                if (init == null)
                    init = new LSharp.SExpNil(context);
                foreach (LSharp.Scope scope in context.LastThreeScopes)
                    scope[name.As<LSharp.SExpId>().Name] = init;
            }

            return name;
        }

        public static object Set(LSharp.RuntimeContext context)
        {
            LSharp.SExp name = null;

            using (LSharp.ScopeRaii sr = new LSharp.ScopeRaii(context))
            {
                LSharp.ListEnumerator en = context.PeekParameterEnumerator();
                name = en.PopLiteral(context);
                LSharp.SExp val = en.PopParameter(context);
                if (!(name is LSharp.SExpId))
                    throw new IdExpectedException("ID expected");
                if (!(val is LSharp.SExp))
                    throw new ParametersExpectedException("Parameter(s) expected");
                LSharp.Scope scope = context.RetrieveScopeWhichContains(name.As<LSharp.SExpId>().Name);
                string idn = name.As<LSharp.SExpId>().Name;
                scope[idn] = val;
                while (scope.InLambda)
                {
                    scope = scope.Prev;

                    scope[idn] = val;
                }
            }

            return name;
        }

        public static object Def(LSharp.RuntimeContext context)
        {
            LSharp.SExpInterpretedFunc func = null;

            using (LSharp.ScopeRaii sr = new LSharp.ScopeRaii(context))
            {
                LSharp.ListEnumerator en = context.PeekParameterEnumerator();
                LSharp.SExp name = en.PopLiteral(context);
                LSharp.SExp args = en.PopLiteral(context);
                LSharp.SExp body = en.PopLiteral(context);
                if (!(name is LSharp.SExpId))
                    throw new IdExpectedException("ID expected");
                else if (!(args is LSharp.SExpList) || !(body is LSharp.SExpList))
                    throw new ListExpectedException("List expected");
                func = new LSharp.SExpInterpretedFunc(context, args.As<LSharp.SExpList>(), body.As<LSharp.SExpList>());
                func.Name = name.As<LSharp.SExpId>().Name;
                foreach (LSharp.Scope scope in context.LastTwoScopes)
                {
                    string _name = name.As<LSharp.SExpId>().Name;
                    if (!scope.ContainsKey(_name))
                        scope[_name] = func;
                }
            }

            return func;
        }

        public static object Undef(LSharp.RuntimeContext context)
        {
            LSharp.SExp result = null;

            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp name = en.PopLiteral(context);
            if (!(name is LSharp.SExpId))
                throw new IdExpectedException("ID expected");

            foreach (LSharp.Scope scope in context.LastTwoScopes)
            {
                string _name = name.As<LSharp.SExpId>().Name;
                if (scope.ContainsKey(_name))
                {
                    result = scope[_name];
                    scope.Remove(_name);
                }
            }

            return result;
        }

        public static object Lambda(LSharp.RuntimeContext context)
        {
            LSharp.SExpLambda lambda = new LSharp.SExpLambda(context);
            LSharp.Scope scope = lambda.Scope;
            List<LSharp.SExpId> upvalues = new List<LSharp.SExpId>();

            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp args = en.PopLiteral(context);
            LSharp.SExp body = en.PopLiteral(context);
            if (!(args is LSharp.SExpList) || !(body is LSharp.SExpList))
                throw new ListExpectedException("List expected");
            lambda.Init(args.As<LSharp.SExpList>(), body.As<LSharp.SExpList>());
            foreach (LSharp.SExp s in (LSharp.SExpList)args)
            {
                if (!(s is LSharp.SExpId))
                    throw new IdExpectedException("ID expected");
                scope[s.As<LSharp.SExpId>().Name] = new LSharp.SExpNil(context);
            }
            body.Foreach
            (
                (_s) =>
                {
                    if (!(_s is LSharp.SExpId)) return;
                    if (scope.ContainsKey(_s.As<LSharp.SExpId>().Name)) return;
                    upvalues.Add(_s.As<LSharp.SExpId>());
                }
            );
            foreach (LSharp.SExpId uv in upvalues)
            {
                LSharp.SExp s = context.RetrieveFromScopeChain(uv.Name);
                if (s != null)
                {
                    LSharp.Scope sp = context.RetrieveScopeWhichContains(uv.Name);
                    lambda.Upvalues[uv] = sp;

                    context.ScopePopped += (_s) =>
                    {
                        if (_s == sp)
                        {
                            LSharp.SExpId id = null;
                            foreach (var kv in lambda.Upvalues)
                            {
                                if (kv.Value == sp)
                                {
                                    id = kv.Key;

                                    break;
                                }
                            }

                            if (id != null)
                                lambda.Upvalues.Remove(id);
                        }
                    };

                    scope[uv.Name] = (LSharp.SExp)s.Clone();
                }
            }

            return lambda;
        }

        public static object Send(LSharp.RuntimeContext context)
        {
            using (LSharp.SendingRaii sending = new LSharp.SendingRaii(context))
            {
                LSharp.ListEnumerator en = context.PeekParameterEnumerator();
                LSharp.SExp id = en.PopLiteral(context);
                LSharp.SExp msg = en.PopParameter(context);
                LSharp.SExp rtv = null;
                if (id is LSharp.SExpId)
                    rtv = context.RetrieveFromScopeChain(id.As<LSharp.SExpId>().Name);
                else
                    rtv = id.Eval();
                List<LSharp.SExp> args = new List<LSharp.SExp>();
                args.Add(msg);
                LSharp.SExp arg = en.TryPopParameter(context);
                while (arg != null)
                {
                    args.Add(arg);
                    arg = en.TryPopParameter(context);
                }

                return rtv.Recv(args.ToArray());
            }
        }

        public static object Foreach(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp obj = en.PopParameter(context);
            //LSharp.SExp op = en.PopParameter(context);
            obj = obj.Eval();

            LSharp.SExp result = null;
            if (!(obj is LSharp.SExpList) && !(obj is LSharp.SExpDict))
                throw new CollectionExpectedException("Collection expected");
            if (obj is LSharp.SExpList)
            {
                foreach (LSharp.SExp s in obj.As<LSharp.SExpList>())
                    result = s.Eval();
                // TODO
            }
            else if (obj is LSharp.SExpDict)
            {
                foreach (LSharp.SExp s in obj.As<LSharp.SExpDict>().Values)
                    result = s.Eval();
                // TODO
            }

            return result;
        }

        public static object Len(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp a = en.PopParameter(context);
            if (a is LSharp.SExpNumber)
                return a.ToNumber().ToString().Length;
            else if (a is LSharp.SExpString)
                return a.ToText().Length;
            else if (a is LSharp.SExpList)
                return a.As<LSharp.SExpList>().Count;
            else if (a is LSharp.SExpDict)
                return a.As<LSharp.SExpDict>().Count;

            return null;
        }

        public static object Print(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp s = en.PopParameter(context);
            context.Interpreter.Printer(s.ToText());

            return null;
        }

        public static object PrintL(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp s = en.PopParameter(context);
            context.Interpreter.Printer(s.ToText() + Environment.NewLine);

            return null;
        }

        public static object Input(LSharp.RuntimeContext context)
        {
            return Console.In.ReadLine();
        }

        public static void Open(LSharp ls)
        {
            ls.Register(new LSharp.SExpNil(ls.Context), "nil");
            ls.Register(0, "false");
            ls.Register(1, "true");

            ls.Register(Import, "import");
            ls.Register(Exec, "exec");
            ls.Register(Is, "is");
            ls.Register(New, "new");
            ls.Register(EnumInt, "enum_int");
            ls.Register(List, "list");
            ls.Register(Cons, "cons");
            ls.Register(Dict, "dict");
            ls.Register(Cond, "cond");
            ls.Register(If, "if");
            ls.Register(Car, "car");
            ls.Register(Cdr, "cdr");
            ls.Register(Eval, "eval");
            ls.Register(Var, "var");
            ls.Register(Set, "set");
            ls.Register(Def, "def");
            ls.Register(Undef, "undef");
            ls.Register(Lambda, "lambda");
            ls.Register(Send, "!");
            ls.Register(Foreach, "foreach");
            ls.Register(Len, "len");
            ls.Register(Print, "print");
            ls.Register(PrintL, "printl");
            ls.Register(Input, "input");
        }

        public static void Close(LSharp ls)
        {
            ls.Unregister("nil");
            ls.Unregister("false");
            ls.Unregister("true");

            ls.Unregister("import");
            ls.Unregister("exec");
            ls.Unregister("is");
            ls.Unregister("new");
            ls.Unregister("enum_int");
            ls.Unregister("list");
            ls.Unregister("cons");
            ls.Unregister("dict");
            ls.Unregister("cond");
            ls.Unregister("if");
            ls.Unregister("car");
            ls.Unregister("cdr");
            ls.Unregister("eval");
            ls.Unregister("var");
            ls.Unregister("set");
            ls.Unregister("def");
            ls.Unregister("undef");
            ls.Unregister("lambda");
            ls.Unregister("!");
            ls.Unregister("foreach");
            ls.Unregister("len");
            ls.Unregister("print");
            ls.Unregister("printl");
            ls.Unregister("input");
        }
    }

    public static class CalcLib
    {
        private static bool IsSimpleOrCallableOrId(LSharp.SExp s)
        {
            return
                s is LSharp.SExpNumber ||
                s is LSharp.SExpString ||
                s is LSharp.SExpHostFunc ||
                s is LSharp.SExpInterpretedFunc ||
                s is LSharp.SExpLambda ||
                s is LSharp.SExpId;
        }

        public static object Add(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            LSharp.SExp c = en.TryPopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
            {
                number ret = (number)(a.ToNumber() + b.ToNumber());

                return ret;
            }
            else if ((a is LSharp.SExpString && b is LSharp.SExpString) ||
                (a is LSharp.SExpString && b is LSharp.SExpNumber) ||
                (a is LSharp.SExpNumber && b is LSharp.SExpString))
            {
                string ret = a.ToText() + b.ToText();

                return ret;
            }
            else
            {
                return a.Recv(func, b, c);
            }
        }

        public static object Min(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
            {
                number ret = (number)(a.ToNumber() - b.ToNumber());

                return ret;
            }
            else if ((a is LSharp.SExpString && b is LSharp.SExpString) ||
                (a is LSharp.SExpString && b is LSharp.SExpNumber) ||
                (a is LSharp.SExpNumber && b is LSharp.SExpString))
            {
                string ret = a.ToText().Replace(b.ToText(), string.Empty);

                return ret;
            }
            else
            {
                return a.Recv(func, b);
            }
        }

        public static object Mul(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
            {
                number ret = (number)(a.ToNumber() * b.ToNumber());

                return ret;
            }
            else if (a is LSharp.SExpString && b is LSharp.SExpNumber)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < (int)b.ToNumber(); i++)
                    sb.Append(a.ToText());
                string ret = sb.ToString();

                return ret;
            }
            else if (a is LSharp.SExpNumber && b is LSharp.SExpString)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < (int)a.ToNumber(); i++)
                    sb.Append(b.ToText());
                string ret = sb.ToString();

                return ret;
            }
            else
            {
                return a.Recv(func, b);
            }
        }

        public static object Div(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
            {
                number ret = (number)(a.ToNumber() / b.ToNumber());

                return ret;
            }
#if !PocketPC
            else if ((a is LSharp.SExpString && b is LSharp.SExpString) ||
                (a is LSharp.SExpString && b is LSharp.SExpNumber) ||
                (a is LSharp.SExpNumber && b is LSharp.SExpString))
            {
                string[] ret = a.ToText().Split(new string[] { b.ToText() }, StringSplitOptions.None);

                return ret;
            }
#endif
            else
            {
                return a.Recv(func, b);
            }
        }

        public static object Lt(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
                return a.ToNumber() < b.ToNumber();
            else if (a is LSharp.SExpString && b is LSharp.SExpNumber)
                return string.Compare(a.ToText(), b.ToText()) < 0;
            else if (a is LSharp.SExpNumber && b is LSharp.SExpString)
                return string.Compare(a.ToText(), b.ToText()) < 0;
            else if (a is LSharp.SExpNil || b is LSharp.SExpNil)
                return a is LSharp.SExpNil && !(b is LSharp.SExpNil);
            else
                return a.Recv(func, b);
        }

        public static object Gt(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
                return a.ToNumber() > b.ToNumber();
            else if (a is LSharp.SExpString && b is LSharp.SExpNumber)
                return string.Compare(a.ToText(), b.ToText()) > 0;
            else if (a is LSharp.SExpNumber && b is LSharp.SExpString)
                return string.Compare(a.ToText(), b.ToText()) > 0;
            else if (a is LSharp.SExpNil || b is LSharp.SExpNil)
                return !(a is LSharp.SExpNil) && b is LSharp.SExpNil;
            else
                return a.Recv(func, b);
        }

        public static object Le(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
                return a.ToNumber() <= b.ToNumber();
            else if (a is LSharp.SExpString && b is LSharp.SExpNumber)
                return string.Compare(a.ToText(), b.ToText()) <= 0;
            else if (a is LSharp.SExpNumber && b is LSharp.SExpString)
                return string.Compare(a.ToText(), b.ToText()) <= 0;
            else if (a is LSharp.SExpNil || b is LSharp.SExpNil)
                return a is LSharp.SExpNil;
            else
                return a.Recv(func, b);
        }

        public static object Ge(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
                return a.ToNumber() >= b.ToNumber();
            else if (a is LSharp.SExpString && b is LSharp.SExpNumber)
                return string.Compare(a.ToText(), b.ToText()) >= 0;
            else if (a is LSharp.SExpNumber && b is LSharp.SExpString)
                return string.Compare(a.ToText(), b.ToText()) >= 0;
            else if (a is LSharp.SExpNil || b is LSharp.SExpNil)
                return b is LSharp.SExpNil;
            else
                return a.Recv(func, b);
        }

        public static object Ne(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
                return a.ToNumber() != b.ToNumber();
            else if (a is LSharp.SExpString && b is LSharp.SExpNumber)
                return string.Compare(a.ToText(), b.ToText()) != 0;
            else if (a is LSharp.SExpNumber && b is LSharp.SExpString)
                return string.Compare(a.ToText(), b.ToText()) != 0;
            else if (a is LSharp.SExpNil || b is LSharp.SExpNil)
                return !(a is LSharp.SExpNil && b is LSharp.SExpNil);
            else
                return a.Recv(func, b);
        }

        public static object Eq(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
                return a.ToNumber() == b.ToNumber();
            else if (a is LSharp.SExpString && b is LSharp.SExpNumber)
                return string.Compare(a.ToText(), b.ToText()) == 0;
            else if (a is LSharp.SExpNumber && b is LSharp.SExpString)
                return string.Compare(a.ToText(), b.ToText()) == 0;
            else if (a is LSharp.SExpString && b is LSharp.SExpString)
                return a.ToText() == b.ToText();
            else if (a is LSharp.SExpNil || b is LSharp.SExpNil)
                return a is LSharp.SExpNil && b is LSharp.SExpNil;
            else
                return a.Recv(func, b);
        }

        public static object Deq(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
                return a.ToNumber() == b.ToNumber();
            else if (a is LSharp.SExpString && b is LSharp.SExpString)
                return string.Compare(a.ToText(), b.ToText()) == 0;
            else if (a is LSharp.SExpNil || b is LSharp.SExpNil)
                return a is LSharp.SExpNil && b is LSharp.SExpNil;
            else
                return a.Recv(func, b);
        }

        public static object Mod(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (a is LSharp.SExpNumber && b is LSharp.SExpNumber)
            {
                number ret = (number)(a.ToNumber() % b.ToNumber());

                return ret;
            }
            else if ((a is LSharp.SExpString && b is LSharp.SExpString) ||
                (a is LSharp.SExpString && b is LSharp.SExpNumber) ||
                (a is LSharp.SExpNumber && b is LSharp.SExpString))
            {
                Regex r = new Regex(b.ToText());
                Match m = r.Match(a.ToText());
                LSharp.SExpList ret = new LSharp.SExpList(context);
                for (int i = 0; i < m.Captures.Count; i++)
                    ret.Add(new LSharp.SExpString(context, m.Captures[i].Value));

                return ret;
            }
            else
            {
                return a.Recv(func, b);
            }
        }

        public static object And(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (IsSimpleOrCallableOrId(a) && IsSimpleOrCallableOrId(b))
                return new LSharp.SExpNumber(context, ((a.ToBool() && b.ToBool()) ? 1 : 0));
            else
                return a.Recv(func, b);
        }

        public static object Or(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            LSharp.SExp b = en.PopParameter(context);
            if (IsSimpleOrCallableOrId(a) && IsSimpleOrCallableOrId(b))
                return new LSharp.SExpNumber(context, ((a.ToBool() || b.ToBool()) ? 1 : 0));
            else
                return a.Recv(func, b);
        }

        public static object Not(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            if (IsSimpleOrCallableOrId(a))
                return new LSharp.SExpNumber(context, (!a.ToBool()) ? 1 : 0);
            else
                return a.Recv(func);
        }

        public static object Str(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            if (a is LSharp.SExpNumber)
                return a.ToText();
            else
                return a.Recv(func);
        }

        public static object Num(LSharp.RuntimeContext context)
        {
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp func = en.Current;
            LSharp.SExp a = en.PopParameter(context);
            if (a is LSharp.SExpString)
                return a.ToNumber();
            else
                return a.Recv(func);
        }

        public static void Open(LSharp ls)
        {
            ls.Register(Math.E, "math.e");
            ls.Register(Math.PI, "math.pi");

            ls.Register(typeof(Math), "Acos", "math.acos");
            ls.Register(typeof(Math), "Asin", "math.asin");
            ls.Register(typeof(Math), "Atan", "math.atan");
            ls.Register(typeof(Math), "Atan2", "math.atan2");
            ls.Register(typeof(Math), "Cos", "math.cos");
            ls.Register(typeof(Math), "Cosh", "math.cosh");
            ls.Register(typeof(Math), "Exp", "math.exp");
            ls.Register(typeof(Math), "Log10", "math.log10");
            ls.Register(typeof(Math), "Pow", "math.pow");
            ls.Register(typeof(Math), "Sin", "math.sin");
            ls.Register(typeof(Math), "Sinh", "math.sinh");
            ls.Register(typeof(Math), "Sqrt", "math.sqrt");
            ls.Register(typeof(Math), "Tan", "math.tan");
            ls.Register(typeof(Math), "Tanh", "math.tanh");
            ls.Register(new Func<number, number>(_a => (number)Math.Abs(_a)), "math.abs");
            ls.Register(new Func<number, number>(_a => (number)Math.Ceiling(_a)), "math.ceil");
            ls.Register(new Func<number, number>(_a => (number)Math.Floor(_a)), "math.floor");
            ls.Register(new Func<number, number>(_a => (number)Math.Round(_a)), "math.round");
            ls.Register(new Func<number, number>(_a => (number)Math.Sign(_a)), "math.sign");
#if !PocketPC
            ls.Register(new Func<number, number>(_a => (number)Math.Truncate(_a)), "math.truncate");
#endif

            ls.Register(Add, "+");
            ls.Register(Min, "-");
            ls.Register(Mul, "*");
            ls.Register(Div, "/");
            ls.Register(Lt, "<");
            ls.Register(Gt, ">");
            ls.Register(Le, "<=");
            ls.Register(Ge, ">=");
            ls.Register(Ne, "~=");
            ls.Register(Eq, "=");
            ls.Register(Deq, "==");
            ls.Register(Mod, "mod");
            ls.Register(And, "and");
            ls.Register(Or, "or");
            ls.Register(Not, "not");
            ls.Register(Str, "str");
            ls.Register(Num, "num");
        }

        public static void Close(LSharp ls)
        {
            ls.Unregister("math.e");
            ls.Unregister("math.pi");

            ls.Unregister("math.acos");
            ls.Unregister("math.asin");
            ls.Unregister("math.atan");
            ls.Unregister("math.atan2");
            ls.Unregister("math.cos");
            ls.Unregister("math.cosh");
            ls.Unregister("math.exp");
            ls.Unregister("math.log10");
            ls.Unregister("math.pow");
            ls.Unregister("math.sin");
            ls.Unregister("math.sinh");
            ls.Unregister("math.sqrt");
            ls.Unregister("math.tan");
            ls.Unregister("math.tanh");
            ls.Unregister("math.abs");
            ls.Unregister("math.ceil");
            ls.Unregister("math.floor");
            ls.Unregister("math.round");
            ls.Unregister("math.sign");
            ls.Unregister("math.truncate");

            ls.Unregister("+");
            ls.Unregister("-");
            ls.Unregister("*");
            ls.Unregister("/");
            ls.Unregister("<");
            ls.Unregister(">");
            ls.Unregister("<=");
            ls.Unregister(">=");
            ls.Unregister("~=");
            ls.Unregister("=");
            ls.Unregister("==");
            ls.Unregister("mod");
            ls.Unregister("and");
            ls.Unregister("or");
            ls.Unregister("not");
            ls.Unregister("str");
            ls.Unregister("num");
        }
    }

    public static class StandardLib
    {
        public static object While(LSharp.RuntimeContext context)
        {
            LSharp.SExp result = null;
            LSharp.ListEnumerator en = context.PeekParameterEnumerator();
            LSharp.SExp a = en.PopLiteral(context);
            LSharp.SExp b = en.PopLiteral(context);
            while (a.ToBool())
                result = b.Eval();

            return result;
        }

        public static void Open(LSharp ls)
        {
            ls.Register(While, "while");

            string stdlib =
                "(def repeat (c d) (while (> c 0) ((! d eval) (set c (- c 1)))))\n";

            LSharp nls = new LSharp(ls);
            nls.LoadString(stdlib);
            nls.Execute();
        }

        public static void Close(LSharp ls)
        {
            ls.Unregister("while");
        }
    }

    #endregion
}
