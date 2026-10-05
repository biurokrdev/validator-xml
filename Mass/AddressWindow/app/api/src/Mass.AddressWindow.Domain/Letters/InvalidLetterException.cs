namespace Mass.AddressWindow.Domain.Letters;

/// <summary>Przesłany plik nie nadaje się do sprawdzenia (pusty, za duży, nie jest PDF-em).</summary>
public sealed class InvalidLetterException(string message) : Exception(message);
