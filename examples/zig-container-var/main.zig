// Milestone R, part 6 — container-level `var` (a namespaced mutable global) +
// a const RHS that references a sibling const by bare (unqualified) name.
//
// `Cfg.counter` is a mutable global namespaced under the struct: dotcc lowers it to a real
// global field (mangled `Cfg_counter`), so `Cfg.counter = …` / `+= …` write through it.
// `Cfg.doubled = base * 2` references the sibling `const base` by bare name — resolved
// against the container's const table (no `Cfg.` qualifier needed).

const Cfg = struct {
    const base: u32 = 10;
    const doubled: u32 = base * 2; // bare `base` → the sibling const ⇒ 20
    var counter: u32 = 0; // a namespaced mutable global
};

pub fn main() u8 {
    Cfg.counter = Cfg.doubled; // 20
    Cfg.counter += Cfg.base; // + 10 = 30
    Cfg.counter += 12; // + 12 = 42
    return @intCast(Cfg.counter);
}
