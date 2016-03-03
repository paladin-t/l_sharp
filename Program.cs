/*
** This source file is a Windows shell program of L#
**
** For the latest info, see https://github.com/paladin-t/l_sharp
**
** Copyright (c) 2012 - 2016 Wang Renxin
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
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;

namespace tony
{
	public class Program
	{
		private static LSharp lsharp = new LSharp();

		private static List<string> program = new List<string>();

		#region Prompts

		private static void ShowTip()
		{
			Console.Out.WriteLine("L# Interpreter Shell - " + LSharp.VERSION_STRING + ".");
			Console.Out.WriteLine("Copyright (c) 2012 - 2016 Tony Wang. All Rights Reserved.");
			Console.Out.WriteLine("For more information, see https://github.com/paladin-t/l_sharp.");
			Console.Out.WriteLine("Input HELP and hint enter to view help information.");
		}

		private static void ShowHelp()
		{
			Console.Out.WriteLine("Commands:");
			Console.Out.WriteLine("  CLS   - Clear screen");
			Console.Out.WriteLine("  NEW   - Clear current program");
			Console.Out.WriteLine("  RUN   - Run current program");
			Console.Out.WriteLine("  BYE   - Quit interpreter");
			Console.Out.WriteLine("  LIST  - List current program");
			Console.Out.WriteLine("          Usage: LIST [l [n]], l is start line number, n is line count");
			Console.Out.WriteLine("  EDIT  - Edit a line in current program");
			Console.Out.WriteLine("          Usage: EDIT n, n is line number");
			Console.Out.WriteLine("  LOAD  - Load a file as current program");
			Console.Out.WriteLine("          Usage: LOAD *.*");
			Console.Out.WriteLine("  SAVE  - Save current program to a file");
			Console.Out.WriteLine("          Usage: SAVE *.*");
			Console.Out.WriteLine("  KILL  - Delete a file");
			Console.Out.WriteLine("          Usage: KILL *.*");
		}

		private static void NewProgram()
		{
			program.Clear();
		}

		private static string GetProgram(int sn, int cn)
		{
			if (sn < 1 || sn > program.Count)
			{
				Console.Out.WriteLine("Line number {0} out of bound.", sn);

				return string.Empty;
			}
			if (cn < 0)
			{
				Console.Out.WriteLine("Invalid line count {0}.", sn);

				return string.Empty;
			}

			StringBuilder sb = new StringBuilder();
			for (int i = sn; i < sn + cn; i++)
			{
				string l = program[i - 1];
				sb.Append(l);
				sb.Append("\n");
			}

			return sb.ToString();
		}

		private static void EditProgram(int sn)
		{
			if (sn < 1 || sn > program.Count)
			{
				Console.Out.WriteLine("Line number {0} out of bound.", sn);

				return;
			}

			Console.Out.Write("{0}]", sn);
			string nl = Console.In.ReadLine();
			program[sn - 1] = nl;
		}

		private static bool DoLine()
		{
			Console.Out.Write(']');
			string line = Console.In.ReadLine();
			string dup = line;
			int si = line.IndexOf(' ');
			if (si != -1)
				line = line.Substring(0, si);

			switch (line.ToUpper())
			{
				case "":
					// Do nothing
					break;
				case "HELP":
					ShowHelp();
					break;
				case "CLS":
					Console.Clear();
					break;
				case "NEW":
					NewProgram();
					break;
				case "RUN":
					try
					{
						if (program.Count == 0)
							break;
						lsharp.ClearExecutable();
						lsharp.LoadString(GetProgram(1, program.Count));
						LSharp.SExp ret = lsharp.Execute();
						Console.Out.WriteLine(ret.ToText());
						Console.Out.WriteLine();
					}
					catch (LSharpException ex)
					{
						Console.Out.WriteLine("Error at ({0}, {1}):", ex.Row, ex.Col);
						Console.Out.WriteLine(ex.Message);
					}
					catch (Exception ex)
					{
						Console.Out.WriteLine("Unknown error:");
						Console.Out.WriteLine(ex.Message);
					}
					break;
				case "BYE":
					return false;
				case "LIST":
					{
						if (program.Count == 0)
						{
							Console.Out.WriteLine(string.Empty);
						}
						else if (dup.Contains(' '))
						{
							string[] parts = dup.Substring(line.Length + 1).Split(' ');
							int sn = int.Parse(parts[0]);
							int cn = program.Count - sn + 1;
							if (parts.Length == 2)
								cn = int.Parse(parts[1]);
							Console.Out.WriteLine(GetProgram(sn, cn));
						}
						else
						{
							Console.Out.WriteLine(GetProgram(1, program.Count));
						}
					}
					break;
				case "EDIT":
					{
						string left = dup.Substring(line.Length + 1).Trim();
						int sn = int.Parse(left);
						EditProgram(sn);
					}
					break;
				case "LOAD":
					{
						try
						{
							string left = dup.Substring(line.Length + 1).Trim();
							program.Clear();
							using (FileStream fs = new FileStream(left, FileMode.Open, FileAccess.Read))
							{
								using (StreamReader sr = new StreamReader(fs))
								{
									while (!sr.EndOfStream)
										program.Add(sr.ReadLine());
								}
							}
						}
						catch (Exception ex)
						{
							Console.Out.WriteLine("Loading error:");
							Console.Out.WriteLine(ex.Message);
						}
					}
					break;
				case "SAVE":
					{
						try
						{
							string left = dup.Substring(line.Length + 1).Trim();
							using (FileStream fs = new FileStream(left, FileMode.OpenOrCreate, FileAccess.Write))
							{
							}
							using (FileStream fs = new FileStream(left, FileMode.Truncate, FileAccess.Write))
							{
								using (StreamWriter sw = new StreamWriter(fs))
								{
									sw.Write(GetProgram(1, program.Count));
								}
							}
						}
						catch (Exception ex)
						{
							Console.Out.WriteLine("Saving error:");
							Console.Out.WriteLine(ex.Message);
						}
					}
					break;
				case "KILL":
					{
						try
						{
							string left = dup.Substring(line.Length + 1).Trim();
							File.Delete(left);
						}
						catch (Exception ex)
						{
							Console.Out.WriteLine("Killing error:");
							Console.Out.WriteLine(ex.Message);
						}
					}
					break;
				default:
					program.Add(dup);
					break;
			}

			return true;
		}

		#endregion

		public static void Main(string[] args)
		{
			try
			{
				Console.ForegroundColor = ConsoleColor.DarkGreen;

				if (args.Length == 1)
				{
					lsharp.LoadFile(args[0]);
					LSharp.SExp ret = lsharp.Execute();
					Console.Out.WriteLine(ret.ToText());
				}
				else if (args.Length == 0)
				{
					ShowTip();
					while (DoLine())
						;
				}
				else
				{
					Console.Out.WriteLine("Unknown arguments.");
				}
			}
			catch (LSharpException ex)
			{
				Console.Out.WriteLine("Error at ({0}, {1}):", ex.Row, ex.Col);
				Console.Out.WriteLine(ex.Message);
			}
			catch (Exception ex)
			{
				Console.Out.WriteLine("Unknown error:");
				Console.Out.WriteLine(ex.Message);
			}
		}
	}
}
