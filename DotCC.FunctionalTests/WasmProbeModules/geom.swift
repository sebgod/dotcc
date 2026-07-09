// WF0 probe fixture: Embedded Swift, Geometry-shaped pure value math.
// struct + computed property + generic (monomorphized) + recursion + f64 math.
struct Vec2 {
    var x: Double
    var y: Double
    func dot(_ o: Vec2) -> Double { x * o.x + y * o.y }
    var length: Double { (x * x + y * y).squareRoot() }
}

func maxOf<T: Comparable>(_ a: T, _ b: T) -> T { a > b ? a : b }

@_expose(wasm, "square")
public func square(_ x: Int32) -> Int32 { x * x }

@_expose(wasm, "vec_length")
public func vecLength(_ x: Double, _ y: Double) -> Double {
    Vec2(x: x, y: y).length
}

@_expose(wasm, "dot")
public func dot(_ ax: Double, _ ay: Double, _ bx: Double, _ by: Double) -> Double {
    Vec2(x: ax, y: ay).dot(Vec2(x: bx, y: by))
}

@_expose(wasm, "imax")
public func imax(_ a: Int32, _ b: Int32) -> Int32 { maxOf(a, b) }

@_expose(wasm, "fib")
public func fib(_ n: Int32) -> Int32 { n < 2 ? n : fib(n - 1) + fib(n - 2) }
