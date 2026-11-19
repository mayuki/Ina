using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

// A tour through runtime features that depend on correct linking: exception handling needs the unwind tables,
// the garbage collector needs stack maps and write barriers, threads need thread-local storage, and so on.
// The output is deterministic so that it can be compared between the JIT and the native executable.
// Lines starting with '#' describe the execution mode and are expected to differ.
var failures = 0;

Section("exceptions", () =>
{
    Check("catch", Catch(() => throw new InvalidOperationException("boom")) == "InvalidOperationException: boom");
    Check("finally order", FinallyOrder() == "try,inner-finally,catch,outer-finally");
    Check("filter", Filter(42) == "filtered 42" && Filter(1) == "unfiltered 1");
    Check("rethrow keeps type", Catch(() =>
    {
        try
        {
            throw new FormatException("first");
        }
        catch (FormatException)
        {
            throw;
        }
    }) == "FormatException: first");
    Check("deep unwind", DeepThrow(200) == 200);
    Check("null reference", Catch(() => Length(null!)) == "NullReferenceException");
    Check("divide by zero", Catch(() => Divide(1, 0)) == "DivideByZeroException");
    Check("index out of range", Catch(() => Store(new int[2], Three())) == "IndexOutOfRangeException");
    Check("stack trace", StackTraceOf(() => Thrower.Throw()).Contains("Thrower.Throw", StringComparison.Ordinal));
});

Section("generics and dispatch", () =>
{
    IShape[] shapes = [new Circle(1), new Rectangle(2, 3), new Square(2)];
    Check("interface dispatch", string.Join(",", shapes.Select(static s => s.Describe())) == "Circle 3.14,Rectangle 6.00,Square 4.00");
    Check("generic sum", Sum([1, 2, 3]) == 6 && Math.Abs(Sum([0.5, 0.25]) - 0.75) < 1e-9);
    Check("generic virtual", new Visitor().Visit(41) == "Int32:41" && new Visitor().Visit("x") == "String:x");
    Func<int, int> twice = static x => x * 2;
    var offset = 10;
    Func<int, int> shift = x => x + offset;
    Check("delegates", twice(shift(1)) == 22);
    Check("linq", Enumerable.Range(1, 100).Where(static x => x % 3 == 0).Select(static x => x * x).Sum() == 112761);
    Check("pattern matching", Classify(5) == "small" && Classify(500) == "large" && Classify("text") == "string of 4" && Classify(null) == "nothing");
    Check("records", new Point(1, 2) == new Point(1, 2) && new Point(1, 2).ToString() == "Point { X = 1, Y = 2 }");
    Check("enums", Enum.GetNames<Color>().Length == 3 && Color.Green.ToString() == "Green" && Enum.Parse<Color>("Blue") == Color.Blue);
});

Section("strings and numbers", () =>
{
    var culture = CultureInfo.InvariantCulture;
    Check("format", string.Format(culture, "{0:N2}|{1:X4}|{2:P1}|{3:E2}", 1234567.891, 255, 0.256, 12345.678) == "1,234,567.89|00FF|25.6 %|1.23E+004");
    Check("interpolation", $"{Math.PI:F4} {int.MaxValue} {long.MinValue} {1.5m * 3}" == "3.1416 2147483647 -9223372036854775808 4.5");
    Check("parse", int.Parse("-123", culture) == -123 && double.Parse("1e3", culture) == 1000 && decimal.Parse("0.1", culture) + 0.2m == 0.3m);
    Check("utf8", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("こんにちは🌏")) == "こんにちは🌏" && Encoding.UTF8.GetByteCount("こんにちは🌏") == 19);
    Check("builder", new StringBuilder().Append("a").Append(1).AppendLine().Append('z', 3).ToString() == $"a1{Environment.NewLine}zzz");
    Check("math", Math.Round(Math.Sin(1) + Math.Cos(2) + Math.Pow(2, 0.5) + Math.Log(10), 6) == 4.142123);
    Check("big numbers", System.Numerics.BigInteger.Pow(2, 100).ToString() == "1267650600228229401496703205376");
    Check("dates", new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddDays(45).ToString("yyyy-MM-dd HH:mm", culture) == "2027-01-02 09:30");
    Check("guid", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff").ToByteArray()[0] == 0x33);
    Check("span", SumSpan(stackalloc int[] { 1, 2, 3, 4 }) == 10);
});

Section("collections", () =>
{
    var dictionary = new Dictionary<string, int>();
    for (var i = 0; i < 10_000; i++)
    {
        dictionary[$"key{i}"] = i;
    }

    Check("dictionary", dictionary.Count == 10_000 && dictionary["key9999"] == 9999 && !dictionary.ContainsKey("missing"));
    var list = Enumerable.Range(0, 1000).Select(static i => (i * 7919) % 1000).ToList();
    list.Sort();
    Check("sort", list.SequenceEqual(Enumerable.Range(0, 1000)));
    Check("hash set", new HashSet<int>(Enumerable.Range(0, 100).Select(static i => i % 10)).Count == 10);
    var queue = new PriorityQueue<string, int>();
    queue.Enqueue("low", 3);
    queue.Enqueue("high", 1);
    Check("priority queue", queue.Dequeue() == "high");
    Check("multi-dimensional array", Fill(new int[3, 4]) == 66);
});

Section("garbage collector", () =>
{
    var before = GC.CollectionCount(0);
    long checksum = 0;
    for (var i = 0; i < 200_000; i++)
    {
        var node = new Node(i, new byte[64]);
        checksum += node.Value + node.Payload.Length;
    }

    Check("allocation", checksum == 20_012_700_000);
    GC.Collect(0);
    Check("collections happened", GC.CollectionCount(0) > before);

    var weak = AllocateWeak();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Check("weak reference cleared", !weak.IsAlive);
    Check("finalizer ran", Finalizable.Finalized > 0);

    // A linked structure that survives collections exercises the write barrier and the card table.
    var head = new Node(0, []);
    var current = head;
    for (var i = 1; i <= 50_000; i++)
    {
        current.Next = new Node(i, []);
        current = current.Next;
        if (i % 10_000 == 0)
        {
            GC.Collect(0);
        }
    }

    var length = 0;
    for (var node = head; node is not null; node = node.Next)
    {
        length++;
    }

    Check("survivors intact", length == 50_001);
    Check("large object", new byte[10 * 1024 * 1024].Length == 10 * 1024 * 1024 && GC.GetTotalMemory(false) > 0);
});

await SectionAsync("threads and tasks", async () =>
{
    var counter = 0;
    var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
    {
        for (var i = 0; i < 10_000; i++)
        {
            Interlocked.Increment(ref counter);
        }
    })).ToList();
    threads.ForEach(static t => t.Start());
    threads.ForEach(static t => t.Join());
    Check("threads", counter == 40_000);

    Check("thread static", ThreadLocalState.Run() == "main=1,other=1,main=2");
    Check("task", await Task.Run(static () => Enumerable.Range(1, 1000).Sum()) == 500_500);
    Check("when all", (await Task.WhenAll(Enumerable.Range(0, 20).Select(static async i =>
    {
        await Task.Delay(1);
        return i;
    }))).Sum() == 190);

    long total = 0;
    Parallel.For(0, 1000, i => Interlocked.Add(ref total, i));
    Check("parallel", total == 499_500);

    var bag = new ConcurrentDictionary<int, int>();
    Parallel.ForEach(Enumerable.Range(0, 500), i => bag[i] = i * 2);
    Check("concurrent dictionary", bag.Count == 500 && bag[250] == 500);

    using var semaphore = new SemaphoreSlim(0);
    var waiter = Task.Run(async () =>
    {
        await semaphore.WaitAsync();
        return "released";
    });
    semaphore.Release();
    Check("semaphore", await waiter == "released");

    var gate = new object();
    var shared = 0;
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
    {
        for (var i = 0; i < 1000; i++)
        {
            lock (gate)
            {
                shared++;
            }
        }
    })));
    Check("lock", shared == 8000);
    Check("async exception", await CatchAsync(static async () =>
    {
        await Task.Yield();
        throw new TimeoutException("late");
    }) == "TimeoutException: late");
});

Section("files and environment", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "ina-runtime-tour-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var file = Path.Combine(directory, "data.txt");
        File.WriteAllLines(file, ["first", "second", "third"]);
        Check("read lines", File.ReadAllLines(file).Length == 3 && File.ReadAllText(file).StartsWith("first", StringComparison.Ordinal));
        using (var stream = new FileStream(file, FileMode.Append))
        {
            stream.Write("fourth\n"u8);
        }

        // WriteAllLines ends lines the way the platform does; the appended bytes are written as they are.
        Check("append", new FileInfo(file).Length == $"first{Environment.NewLine}second{Environment.NewLine}third{Environment.NewLine}fourth\n".Length);
        Check("enumerate", Directory.GetFiles(directory).Length == 1);
        File.Delete(file);
        Check("delete", !File.Exists(file));
        Check("missing file", Catch(() => File.ReadAllText(file)) is { } message && message.StartsWith("FileNotFoundException", StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    Check("environment", Environment.ProcessorCount > 0 && Environment.GetEnvironmentVariable("PATH") is not null && Environment.TickCount64 > 0);
    Check("stopwatch", System.Diagnostics.Stopwatch.GetTimestamp() > 0 && DateTime.UtcNow.Year >= 2025);
    Check("random", new Random(42).Next(1000) == new Random(42).Next(1000));
});

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");
Console.WriteLine($"# dynamic code: {RuntimeFeature.IsDynamicCodeSupported}");
return failures == 0 ? 0 : 1;

void Section(string name, Action body)
{
    Console.WriteLine($"[{name}]");
    try
    {
        body();
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"  FAIL unexpected {e.GetType().Name}: {e.Message}");
    }
}

async Task SectionAsync(string name, Func<Task> body)
{
    Console.WriteLine($"[{name}]");
    try
    {
        await body();
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"  FAIL unexpected {e.GetType().Name}: {e.Message}");
    }
}

void Check(string name, bool condition)
{
    if (!condition)
    {
        failures++;
    }

    Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {name}");
}

static string Catch(Action action)
{
    try
    {
        action();
        return "no exception";
    }
    catch (Exception e)
    {
        return e.Message.Length > 0 && e is not (NullReferenceException or DivideByZeroException or IndexOutOfRangeException)
            ? $"{e.GetType().Name}: {e.Message}"
            : e.GetType().Name;
    }
}

static async Task<string> CatchAsync(Func<Task> action)
{
    try
    {
        await action();
        return "no exception";
    }
    catch (Exception e)
    {
        return $"{e.GetType().Name}: {e.Message}";
    }
}

static string StackTraceOf(Action action)
{
    try
    {
        action();
        return string.Empty;
    }
    catch (Exception e)
    {
        return e.StackTrace ?? string.Empty;
    }
}

static string FinallyOrder()
{
    var steps = new List<string>();
    try
    {
        try
        {
            steps.Add("try");
            throw new InvalidOperationException();
        }
        finally
        {
            steps.Add("inner-finally");
        }
    }
    catch (InvalidOperationException)
    {
        steps.Add("catch");
    }
    finally
    {
        steps.Add("outer-finally");
    }

    return string.Join(",", steps);
}

static string Filter(int value)
{
    try
    {
        throw new ArgumentException(value.ToString(CultureInfo.InvariantCulture));
    }
    catch (ArgumentException e) when (e.Message == "42")
    {
        return $"filtered {e.Message}";
    }
    catch (ArgumentException e)
    {
        return $"unfiltered {e.Message}";
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static int DeepThrow(int depth)
{
    try
    {
        Recurse(depth);
        return -1;
    }
    catch (InvalidOperationException e)
    {
        return int.Parse(e.Message, CultureInfo.InvariantCulture);
    }

    static void Recurse(int remaining)
    {
        if (remaining == 0)
        {
            throw new InvalidOperationException("200");
        }

        Recurse(remaining - 1);
        GC.KeepAlive(remaining);
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static int Length(string text) => text.Length;

[MethodImpl(MethodImplOptions.NoInlining)]
static int Divide(int a, int b) => a / b;

[MethodImpl(MethodImplOptions.NoInlining)]
static int Three() => 3;

[MethodImpl(MethodImplOptions.NoInlining)]
static void Store(int[] array, int index) => array[index] = 1;

static T Sum<T>(T[] values)
    where T : System.Numerics.INumber<T>
{
    var sum = T.Zero;
    foreach (var value in values)
    {
        sum += value;
    }

    return sum;
}

static string Classify(object? value) => value switch
{
    int n when n < 100 => "small",
    int => "large",
    string { Length: var length } => $"string of {length}",
    null => "nothing",
    _ => "other",
};

static int SumSpan(ReadOnlySpan<int> values)
{
    var sum = 0;
    foreach (var value in values)
    {
        sum += value;
    }

    return sum;
}

static int Fill(int[,] grid)
{
    var sum = 0;
    for (var row = 0; row < grid.GetLength(0); row++)
    {
        for (var column = 0; column < grid.GetLength(1); column++)
        {
            grid[row, column] = row * grid.GetLength(1) + column;
            sum += grid[row, column];
        }
    }

    return sum;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference AllocateWeak()
{
    _ = new Finalizable();
    return new WeakReference(new object());
}

internal interface IShape
{
    double Area { get; }

    string Describe() => $"{GetType().Name} {Area.ToString("F2", CultureInfo.InvariantCulture)}";
}

internal sealed record Circle(double Radius) : IShape
{
    public double Area => Math.PI * Radius * Radius;
}

internal record Rectangle(double Width, double Height) : IShape
{
    public double Area => Width * Height;
}

internal sealed record Square(double Side) : Rectangle(Side, Side);

internal sealed class Visitor
{
    public string Visit<T>(T value) => $"{typeof(T).Name}:{value}";
}

internal readonly record struct Point(int X, int Y);

internal enum Color
{
    Red,
    Green,
    Blue,
}

internal sealed class Node(int value, byte[] payload)
{
    public int Value { get; } = value;

    public byte[] Payload { get; } = payload;

    public Node? Next { get; set; }
}

internal sealed class Finalizable
{
    public static int Finalized;

    ~Finalizable() => Interlocked.Increment(ref Finalized);
}

internal static class Thrower
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Throw() => throw new NotSupportedException("trace me");
}

internal static class ThreadLocalState
{
    [ThreadStatic]
    private static int _value;

    public static string Run()
    {
        var results = new List<string> { $"main={++_value}" };
        var thread = new Thread(() => results.Add($"other={++_value}"));
        thread.Start();
        thread.Join();
        results.Add($"main={++_value}");
        return string.Join(",", results);
    }
}
