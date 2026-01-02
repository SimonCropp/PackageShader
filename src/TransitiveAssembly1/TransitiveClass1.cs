public static class TransitiveClass1
{
    public static string Method() =>
        "TransitiveAssembly1";

    public static string TransitiveMethod() =>
        TransitiveClass2.Method();
}
