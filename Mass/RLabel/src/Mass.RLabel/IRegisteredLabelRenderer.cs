namespace Mass.RLabel;

/// <summary>
/// Generuje grafikę nalepki „R” listu poleconego: literę R w ramce, kod kreskowy numeru nadawczego
/// i numer w postaci czytelnej. Implementacja jest bezstanowa i bezpieczna wątkowo.
/// </summary>
public interface IRegisteredLabelRenderer
{
    /// <summary>Generuje nalepkę według pełnych parametrów.</summary>
    /// <exception cref="ArgumentException">Numer ma zły format lub złą cyfrę kontrolną, albo wymiary są niepoprawne.</exception>
    RegisteredLabelImage Render(RegisteredLabelRequest request);

    /// <summary>Generuje nalepkę i zwraca ją jako Base64 (bez prefiksu <c>data:</c>).</summary>
    /// <param name="number">Numer nadawczy, może być w postaci z nalepki (nawias, spacje).</param>
    /// <param name="type">Krajowa albo zagraniczna.</param>
    /// <param name="widthMm">Szerokość w mm; domyślnie 65.</param>
    /// <param name="heightMm">Wysokość w mm; domyślnie 25.</param>
    /// <param name="format">PNG (domyślnie) albo JPEG.</param>
    /// <exception cref="ArgumentException">Numer ma zły format lub złą cyfrę kontrolną, albo wymiary są niepoprawne.</exception>
    string RenderBase64(
        string number,
        MailType type,
        double widthMm = RegisteredLabelRequest.DefaultWidthMm,
        double heightMm = RegisteredLabelRequest.DefaultHeightMm,
        LabelImageFormat format = LabelImageFormat.Png);
}
