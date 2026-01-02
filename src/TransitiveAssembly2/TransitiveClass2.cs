public static class TransitiveClass2
{
    public static string Method() =>
        "TransitiveAssembly2";

    public static string TransitiveMethod() =>
        TransitiveClass3.Method();
}
