namespace Mass.AddressWindow;

public interface IAddressWindowValidator
{
    AddressWindowValidationResult Validate(Stream document, WindowMode mode, ValidationProfile? profile = null);

    AddressWindowValidationResult Validate(byte[] document, WindowMode mode, ValidationProfile? profile = null);

    AddressWindowValidationResult ValidateFile(string path, WindowMode mode, ValidationProfile? profile = null);

    Task<AddressWindowValidationResult> ValidateAsync(
        Stream document,
        WindowMode mode,
        ValidationProfile? profile = null,
        CancellationToken cancellationToken = default);
}
