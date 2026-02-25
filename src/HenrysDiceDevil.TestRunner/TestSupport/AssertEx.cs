namespace HenrysDiceDevil.Tests.TestSupport;

internal static class AssertEx
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}");
        }
    }

    public static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{message} Expected exception {typeof(TException).Name} but got {ex.GetType().Name}.");
        }

        throw new InvalidOperationException($"{message} Expected exception {typeof(TException).Name} but no exception was thrown.");
    }
}
