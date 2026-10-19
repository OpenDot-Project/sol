module fixtures.control

func sum(values: []int): int {
    var total = 0
    for var i: int = 0; i < values.length; i += 1 {
        if values[i] < 0 {
            continue
        } else {
            total += values[i]
        }
    }
    for val value in values {
        total += value
    }
    for total < 100 {
        total += 1
    }
    for {
        break
    }
    switch total {
        case 0:
            return 0
        default:
            return total
    }
}

func handle(command: Command) {
    switch command {
        case Command.Move(x, _):
            move(x)
        case is Failure:
            report()
        default:
            return
    }
}

func readValue(): int {
    defer { close() }
    val result = try {
        read()
    } catch (error: IOException) {
        fallback()
    } finally {
        release()
    }
    unsafe { nativeCall() }
    result
}

val callback = done@ { ref x: int, out y: int ->
    y = x
    return@done
}
val anonymous = func(x: int): int { return x + 1 }
val transformed = combine(20, 22) { x, y -> x + y }
