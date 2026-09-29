"""The fannkuch-redux benchmark: list slicing, reversal and swaps over every
permutation of 0..n-1, with a checksum."""


def fannkuch(n):
    perm1 = list(range(n))
    count = [0] * n
    max_flips = 0
    checksum = 0
    permutation = 0
    r = n
    while True:
        while r != 1:
            count[r - 1] = r
            r -= 1
        perm = perm1[:]
        flips = 0
        k = perm[0]
        while k:
            perm[:k + 1] = perm[k::-1]
            flips += 1
            k = perm[0]
        max_flips = max(max_flips, flips)
        checksum += flips if permutation % 2 == 0 else -flips
        while True:
            if r == n:
                return checksum, max_flips
            perm0 = perm1[0]
            perm1[:r] = perm1[1:r + 1]
            perm1[r] = perm0
            count[r] -= 1
            if count[r] > 0:
                break
            r += 1
        permutation += 1


checksum, flips = fannkuch(8)
print(checksum)
print(f"Pfannkuchen(8) = {flips}")
