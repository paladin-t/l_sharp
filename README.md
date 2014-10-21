L#
------------

###Introduction
I created L# when I was dedicating in a Unity3D game project in 2012.

L# is a Lisp-like scripting language interpreter for .NET/Mono written with C#.
It's aimed at being either a standalone interpreter or an embedded script. It's
a small size interpreter in all of implementation/runtime/syntax perspectives.
You can easily copy the only implementation source file "LSharp.cs" into your
desktop softwares, console games, smartphone apps, web pages, etc. projects and
benefit from it.

###How to Use
* [Hello L#]

It's quite easy to make L# on the go. The following tutorial gives a simple
example on how to write a "Hello World" in L#.

```
LSharp lsharp = new LSharp();                  // Initialize L#.
lsharp.Printer = (t) => { print(t); };         // Optional, customize the 'print' statement in Unity3D.
lsharp.LoadString("(printl \"hello world\")"); // Load and parse a script string.
lsharp.Execute();                              // Let's rock!
```

* [Variable]

```
(var a "hello world") # Declare a variable "a" and initialize with a string.
(set a "changed")     # Set "a" with a new value.
(printl a)
```

* [Evaluation]

L# use prefix expression for evaluation.

```
(var a 1)             # Declare and set a = 1.
(var b 2)             # Declare and set b = 2.
(set a (* (+ a b) b)) # a = (a + b) * b.
(printl a)
```

* [List Operation]

```
(printl (list 1 2 3))        # Construct a list.
(printl (cons 1 (list 2 3))) # Join expressions into a list.
(printl (car &(1 2 3)))      # Get the first element of a list, prints "1".
(printl (cdr &(1 2 3)))      # Get the rest elements after [0], prints "(2 3)".
```

* [Dictionary Operation]

```
(var d (dict "a" 1 "b" 2 "c" 3)) # Construct a dictionary with ("a": 1, "b": 2, "c": 3).
(! d + "b" 3.14)                 # Set "b" with 3.14.
(printl (! d "b"))               # Fetch the value of key "b".
```

Note L# uses exclamation mark to send a message to an object, the pattern of a
sending statement is (! object message optional_arguments). A message can be a
property/method of a .NET object or a buildin function of an L# object.

* [Use a Function]

L# supports user defined function with pattern (def func_name (par0 par1 ... par_n) (func_body)).

```
(def foo (a b)
    (+ a b)
)
(print (foo 1 2))
```

* [Lambda Expression]

```
(var c 0)               # A global variable.
(var counter (lambda () # Declare a lambda.
    (set c (+ c 1)))
)
(counter)               # Eval once.
(counter)               # Eval twice.
(printl (! counter c))  # Fetch the value in a closure.
(printl c)
```

* [Condition and Iteration]

Single condition.

```
(var foo input) # Initialize "foo" with an input.
(if
    (== foo "1") (print "uno")
    (print "unknown")
)
```

Multiple condition.

```
(var foo input)
(cond
    (== foo "1") (print "uno")
    (== foo "2") (print "dos")
    (== foo "3") (print "tres")
    nil (print "unknown")
)
```

"While" iteration.

```
(var t 5)
(while
    (> t 0)
    (
        (print "@")
        (set t (- t 1))
    )
)
```

"Repeat" iteration.

```
(repeat 5 &(print "@"))
```

* [Import External Library]

Use "import" statement to import functionallities from another L# script or a .NET assembly.

* [Register Host Function]

Search with keyword "Register" in LSharp.cs to get an introduction.

###TODO
1. Polish README document.
2. Write more samples.
