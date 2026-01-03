
public class Person
{
    public required string Name { get; init; }
    public required string Email { get; init; }
}

public class PersonValidator : AbstractValidator<Person>
{
    public static bool OnFailureCalled;
    public static bool OnAnyFailureCalled;
    public PersonValidator()
    {
        RuleFor(_ => _.Name)
            .NotEmpty()
            .OnFailure(_ => OnFailureCalled = true);

        RuleFor(x => x.Email)
            .EmailAddress()
            .OnAnyFailure(_ => OnAnyFailureCalled = true);
    }
}

public static class Validator
{
    public static IEnumerable<string> Run()
    {
        var validator = new PersonValidator();
        var person = new Person
        {
            Name = "",
            Email = "invalid-email"
        };

        var result = validator.Validate(person);
        return result.Errors.Select(_ => _.ErrorMessage);
    }
}
