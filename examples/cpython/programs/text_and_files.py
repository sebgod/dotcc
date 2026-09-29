"""Unicode text and the io stack: non-ASCII case mapping, astral characters,
codecs and their error handlers, str.format alignment, and real file I/O
(text and binary modes, newline translation, seek/tell, os.stat, listdir)
in a scratch directory under the current one, removed at the end. No \\N{...}
escapes: those need the unicodedata module, which the build does not have yet."""

import os
import shutil

s = "Grüße, Ωμέγα! 🐍 ﬁ"
print(len(s), s.upper(), s.lower(), s.casefold(), s.swapcase())
print([hex(ord(c)) for c in "🐍é"], chr(0x1F40D), "α", ascii(s))
for enc in ("utf-8", "utf-16-le", "latin-1", "ascii"):
    print(enc, s.encode(enc, errors="replace")[:24], len(s.encode(enc, errors="backslashreplace")))
print(b"caf\xe9".decode("utf-8", errors="replace"), b"caf\xe9".decode("latin-1"), "é".encode("ascii", "xmlcharrefreplace"))
print(f"[{'left':<10}] [{'right':>10}] [{'mid':^10}] [{3.14159:+08.3f}] [{255:#06x}] [{1234567:_d}]")
print("ǅungla".istitle(), "Straße".isidentifier(), "٣".isdigit(), "½".isnumeric(), "x\ty".expandtabs(4))
print(sorted(["b", "A", "é", "a", "Z"]), sorted(["b", "A", "é", "a", "Z"], key=str.casefold))

base = os.path.join(os.getcwd(), "dotcc-text-files.tmp")
if os.path.exists(base):
    shutil.rmtree(base)
os.mkdir(base)
try:
    text_path = os.path.join(base, "notes.txt")
    with open(text_path, "w", encoding="utf-8", newline="\n") as f:
        for i in range(3):
            f.write(f"line {i}: {s}\n")
    with open(text_path, encoding="utf-8") as f:
        lines = f.readlines()
    print(len(lines), lines[1].rstrip())
    with open(text_path, "rb") as f:
        raw = f.read()
        f.seek(5)
        print("bytes:", len(raw), f.read(1), f.tell(), raw.count(b"\n"))
    with open(text_path, "a", encoding="utf-8", newline="\n") as f:
        print("appended at", f.tell() == len(raw), file=f)
    print("size:", os.stat(text_path).st_size, "exists:", os.path.isfile(text_path))

    bin_path = os.path.join(base, "data.bin")
    with open(bin_path, "wb") as f:
        f.write(bytes(range(256)) * 4)
    with open(bin_path, "r+b") as f:
        f.seek(-4, os.SEEK_END)
        tail = f.read()
        f.seek(0)
        f.write(b"\xff\xfe")
    with open(bin_path, "rb") as f:
        head = f.read(4)
    print("tail:", tail.hex(), "head:", head.hex())

    os.makedirs(os.path.join(base, "sub", "deeper"))
    with open(os.path.join(base, "sub", "deeper", "leaf.txt"), "w") as f:
        f.write("leaf")
    tree = []
    for root, dirs, files in os.walk(base):
        rel = os.path.relpath(root, base).replace(os.sep, "/")
        tree.append((rel, sorted(dirs), sorted(files)))
    print(sorted(tree))
    os.rename(bin_path, bin_path + ".old")
    print(sorted(os.listdir(base)))
finally:
    shutil.rmtree(base)
print("cleaned:", not os.path.exists(base))
