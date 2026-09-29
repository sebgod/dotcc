"""The spectral-norm benchmark: nested generator expressions, zip, sum and
float division; the square root is ** 0.5."""


def eval_a(i, j):
    return 1.0 / ((i + j) * (i + j + 1) // 2 + i + 1)


def eval_a_times_u(u):
    return [sum(eval_a(i, j) * uj for j, uj in enumerate(u)) for i in range(len(u))]


def eval_at_times_u(u):
    return [sum(eval_a(j, i) * uj for j, uj in enumerate(u)) for i in range(len(u))]


def eval_ata_times_u(u):
    return eval_at_times_u(eval_a_times_u(u))


def spectral_norm(n):
    u = [1.0] * n
    for _ in range(10):
        v = eval_ata_times_u(u)
        u = eval_ata_times_u(v)
    vbv = sum(ue * ve for ue, ve in zip(u, v))
    vv = sum(ve * ve for ve in v)
    return (vbv / vv) ** 0.5


print(f"{spectral_norm(100):.9f}")
