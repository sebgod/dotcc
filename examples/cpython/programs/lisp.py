"""A small Scheme interpreter as a "real program": a regex tokenizer, a
recursive parser, environments as dicts in a chain, closures, tail calls by
trampoline, and a few programs run on it."""

import re

TOKEN = re.compile(r"\s*(?:(;[^\n]*)|([()'])|(\"(?:\\.|[^\"])*\")|([^\s()']+))")


class Symbol(str):
    pass


def tokenize(text):
    pos = 0
    tokens = []
    while pos < len(text):
        m = TOKEN.match(text, pos)
        if not m or m.end() == pos:
            break
        pos = m.end()
        comment, paren, string, atom = m.groups()
        if paren:
            tokens.append(paren)
        elif string:
            tokens.append(("str", string[1:-1].encode().decode("unicode_escape")))
        elif atom:
            tokens.append(atom)
    return tokens


def parse(tokens):
    def read(i):
        tok = tokens[i]
        if tok == "(":
            items = []
            i += 1
            while tokens[i] != ")":
                item, i = read(i)
                items.append(item)
            return items, i + 1
        if tok == "'":
            item, i = read(i + 1)
            return [Symbol("quote"), item], i
        if isinstance(tok, tuple):
            return tok[1], i + 1
        for conv in (int, float):
            try:
                return conv(tok), i + 1
            except ValueError:
                pass
        return Symbol(tok), i + 1

    forms = []
    i = 0
    while i < len(tokens):
        form, i = read(i)
        forms.append(form)
    return forms


class Env(dict):
    def __init__(self, names=(), values=(), outer=None):
        super().__init__(zip(names, values))
        self.outer = outer

    def find(self, name):
        env = self
        while env is not None:
            if name in env:
                return env
            env = env.outer
        raise NameError(f"unbound: {name}")


class Lambda:
    def __init__(self, params, body, env):
        self.params, self.body, self.env = params, body, env

    def __repr__(self):
        return f"#<lambda ({' '.join(self.params)})>"


def standard_env():
    env = Env()
    env.update({
        "+": lambda *a: sum(a), "-": lambda a, b=None: -a if b is None else a - b,
        "*": lambda a, b: a * b, "/": lambda a, b: a / b, "quotient": lambda a, b: a // b,
        "remainder": lambda a, b: a % b, "<": lambda a, b: a < b, ">": lambda a, b: a > b,
        "=": lambda a, b: a == b, "<=": lambda a, b: a <= b, "not": lambda a: a is False,
        "car": lambda l: l[0], "cdr": lambda l: l[1:], "cons": lambda a, l: [a] + l,
        "list": lambda *a: list(a), "null?": lambda l: l == [], "length": len,
        "display": lambda *a: print(*[to_str(x) for x in a]), "string-append": lambda *a: "".join(a),
        "number->string": str, "apply": lambda f, args: call(f, args),
    })
    return env


def to_str(x):
    if x is True:
        return "#t"
    if x is False:
        return "#f"
    if isinstance(x, list):
        return "(" + " ".join(to_str(e) for e in x) + ")"
    return str(x)


def call(f, args):
    if isinstance(f, Lambda):
        return evaluate(f.body, Env(f.params, args, f.env))
    return f(*args)


def evaluate(x, env):
    while True:
        if isinstance(x, Symbol):
            return env.find(x)[x]
        if not isinstance(x, list):
            return x
        op, *args = x
        if op == "quote":
            return args[0]
        if op == "if":
            test, conseq, alt = args
            x = conseq if evaluate(test, env) is not False else alt
            continue
        if op == "define":
            name, value = args
            if isinstance(name, list):
                env[name[0]] = Lambda(name[1:], value, env)
            else:
                env[name] = evaluate(value, env)
            return None
        if op == "lambda":
            return Lambda(args[0], args[1], env)
        if op == "let":
            bindings, body = args
            names = [b[0] for b in bindings]
            values = [evaluate(b[1], env) for b in bindings]
            env = Env(names, values, env)
            x = body
            continue
        if op == "begin":
            for e in args[:-1]:
                evaluate(e, env)
            x = args[-1]
            continue
        f = evaluate(op, env)
        vals = [evaluate(a, env) for a in args]
        if isinstance(f, Lambda):
            env = Env(f.params, vals, f.env)
            x = f.body
            continue
        return f(*vals)


PROGRAM = r"""
; recursion, closures, higher-order functions and a tail-recursive loop
(define (fib n) (if (< n 2) n (+ (fib (- n 1)) (fib (- n 2)))))
(define (map f l) (if (null? l) '() (cons (f (car l)) (map f (cdr l)))))
(define (filter p l) (if (null? l) '() (if (p (car l)) (cons (car l) (filter p (cdr l))) (filter p (cdr l)))))
(define (range a b) (if (> a b) '() (cons a (range (+ a 1) b))))
(define (make-counter) (let ((n 0)) (lambda () (begin (define n (+ n 1)) n))))
(define (loop i acc) (if (= i 0) acc (loop (- i 1) (+ acc i))))
(define (compose f g) (lambda (x) (f (g x))))
(display "fib 20 =" (fib 20))
(display "squares" (map (lambda (x) (* x x)) (range 1 10)))
(display "evens" (filter (lambda (x) (= (remainder x 2) 0)) (range 1 20)))
(display "tail loop" (loop 100000 0))
(display "compose" ((compose (lambda (x) (+ x 1)) (lambda (x) (* x 10))) 4))
(display "apply" (apply + (list 1 2 3 4)) (string-append "a" "b" "c") (number->string 3.5))
(display "quote" '(1 (2 three) "four"))
"""

env = standard_env()
for form in parse(tokenize(PROGRAM)):
    evaluate(form, env)
