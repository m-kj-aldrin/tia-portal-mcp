global using static Expect;

internal static class Expect
{
    public static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
