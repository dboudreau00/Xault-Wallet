using System.Diagnostics;
using System.Reflection;
using Xunit;
using Xunit.Sdk;

namespace XaultWallet.TestRunner;

/// <summary>
/// Minimal xUnit runner that discovers and executes [Fact] / [Theory] methods by
/// reflection over this assembly (the test sources are compiled in — see the csproj).
/// Exists because Smart App Control-style environments block the VSTest host, making
/// `dotnet test` silently discover zero tests. Exit code 0 = all passed.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;

        var failures = new List<string>();
        int passed = 0, skipped = 0;
        var sw = Stopwatch.StartNew();

        IEnumerable<Type> classes = typeof(Program).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.GetMethods().Any(m => m.GetCustomAttribute<FactAttribute>() is not null))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (Type type in classes)
        {
            foreach (MethodInfo method in type.GetMethods().OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                FactAttribute? fact = method.GetCustomAttribute<FactAttribute>();
                if (fact is null)
                {
                    continue;
                }

                string name = $"{type.Name}.{method.Name}";
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(fact.Skip))
                {
                    skipped++;
                    continue;
                }

                // A [Theory] runs once per data row; a plain [Fact] runs once with no args.
                List<object?[]> rows;
                try
                {
                    rows = method.GetCustomAttribute<TheoryAttribute>() is not null
                        ? method.GetCustomAttributes<DataAttribute>()
                            .SelectMany(d => d.GetData(method) ?? Enumerable.Empty<object[]>())
                            // [InlineData(null)] surfaces as a null ROW via raw reflection
                            // (the params object[] binds null); treat it as all-null args.
                            .Select(r => (object?[]?)r ?? new object?[method.GetParameters().Length])
                            .ToList()
                        : new List<object?[]> { Array.Empty<object?>() };
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: could not enumerate [Theory] data — {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (rows.Count == 0)
                {
                    failures.Add($"{name}: [Theory] has no data rows");
                    continue;
                }

                foreach (object?[] row in rows)
                {
                    string caseName = row.Length == 0
                        ? name
                        : $"{name}({string.Join(", ", row.Select(v => v is null ? "null" : v.ToString()))})";
                    try
                    {
                        RunOne(type, method, row);
                        passed++;
                    }
                    catch (Exception ex)
                    {
                        Exception real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                        failures.Add($"{caseName}\n    {real.GetType().Name}: {real.Message.ReplaceLineEndings("\n    ")}");
                    }
                }
            }
        }

        sw.Stop();
        foreach (string f in failures)
        {
            Console.WriteLine($"FAIL {f}\n");
        }

        Console.WriteLine($"Tests: {passed} passed, {failures.Count} failed, {skipped} skipped ({sw.Elapsed.TotalSeconds:0.0}s)");

        // Zero tests executed is a FAILURE, not a pass: the csproj's test-source glob silently
        // matches nothing if the tests move — exactly the false-green this runner exists to kill.
        if (passed + failures.Count == 0)
        {
            Console.WriteLine(filter is null
                ? "FATAL: zero tests were discovered — the test-source include in TestRunner.csproj matched nothing."
                : $"FATAL: no test matched filter '{filter}'.");
            return 1;
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private static void RunOne(Type type, MethodInfo method, object?[] row)
    {
        object? instance = method.IsStatic ? null : Activator.CreateInstance(type);
        try
        {
            object? result = method.Invoke(instance, row.Length == 0 ? null : CoerceArgs(method, row));
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
            }
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    /// <summary>xUnit converts InlineData literals to the parameter types (e.g. int/double
    /// attribute values to a decimal parameter); raw MethodInfo.Invoke does not, so mirror it.</summary>
    private static object?[] CoerceArgs(MethodInfo method, object?[] row)
    {
        ParameterInfo[] pars = method.GetParameters();
        var coerced = new object?[row.Length];
        for (int i = 0; i < row.Length; i++)
        {
            object? v = row[i];
            if (v is not null && i < pars.Length)
            {
                Type target = Nullable.GetUnderlyingType(pars[i].ParameterType) ?? pars[i].ParameterType;
                if (!target.IsInstanceOfType(v) && v is IConvertible && target.IsAssignableTo(typeof(IConvertible)))
                {
                    v = Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            coerced[i] = v;
        }

        return coerced;
    }
}
