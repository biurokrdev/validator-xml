using Mass.Api.Contracts;
using Mass.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Mass.Api.Controllers;

/// <summary>
/// Administracja pulą numerów nadawczych przesyłek poleconych (R).
/// Autor zmiany jest brany z nagłówka <c>X-Editor</c> (docelowo z tożsamości użytkownika).
/// </summary>
[ApiController]
[Route("api/registered-numbers")]
[Produces("application/json")]
public sealed class RegisteredNumbersController(IRegisteredNumberPoolRepository repo) : ControllerBase
{
    public const string EditorHeader = "X-Editor";
    private const string DefaultEditor = "api";

    // ------------------------------------------------------------ lista / podgląd

    /// <summary>Lista numerów z filtrami (typ, stan, fragment numeru) i stronicowaniem.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<RegisteredNumberDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<RegisteredNumberDto>>> List(
        [FromQuery] RegisteredNumberPoolType? type,
        [FromQuery] RegisteredNumberPoolState? state,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await repo.ListAsync(new RegisteredNumberQuery(type, state, search, page, pageSize), ct);

        return Ok(new PagedResponse<RegisteredNumberDto>(
            result.Items.Select(RegisteredNumberDto.From).ToList(),
            result.Page, result.PageSize, result.TotalCount, result.TotalPages));
    }

    /// <summary>Statystyki obu pul (dostępne / zarezerwowane / użyte / anulowane).</summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(IReadOnlyList<PoolStatisticsDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PoolStatisticsDto>>> Stats(CancellationToken ct)
    {
        var list = new List<PoolStatisticsDto>();
        foreach (var type in Enum.GetValues<RegisteredNumberPoolType>())
            list.Add(PoolStatisticsDto.From(await repo.GetStatisticsAsync(type, ct)));
        return Ok(list);
    }

    /// <summary>Szczegóły numeru. 400 gdy numer formalnie błędny, 404 gdy spoza puli.</summary>
    [HttpGet("{fullNumber}")]
    [ProducesResponseType(typeof(RegisteredNumberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegisteredNumberDto>> Get(string fullNumber, CancellationToken ct)
    {
        var validation = repo.Validate(fullNumber);
        if (!validation.IsFormatValid)
            return Problem(validation.Error, statusCode: StatusCodes.Status400BadRequest, title: "Nieprawidłowy numer.");

        var entity = await repo.FindAsync(fullNumber, ct);
        return entity is null
            ? Problem($"Numer {validation.FullNumber} nie istnieje w puli.", statusCode: StatusCodes.Status404NotFound, title: "Numer nie istnieje w puli.")
            : Ok(RegisteredNumberDto.From(entity));
    }

    // ------------------------------------------------------------ walidacja

    /// <summary>Walidacja numeru: format, cyfra kontrolna, obecność w puli i stan.</summary>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(ValidationResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ValidationResponse>> Validate([FromBody] ValidateRequest request, CancellationToken ct)
        => Ok(ValidationResponse.From(await repo.ValidateAsync(request.Number, ct)));

    // ------------------------------------------------------------ zasilanie z przedziału

    /// <summary>
    /// Sprawdza przedział przed importem: ile numerów jest nowych, ile już mamy (i w jakim stanie).
    /// Nic nie zapisuje. Służy do weryfikacji, czy zaczytywane R-ki nie są tymi, które już są w puli.
    /// </summary>
    [HttpPost("ranges/check")]
    [ProducesResponseType(typeof(RangeCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RangeCheckResponse>> CheckRange([FromBody] RangeRequest request, CancellationToken ct)
    {
        var range = request.ToRange();   // ArgumentException -> 400 przez handler
        return Ok(RangeCheckResponse.From(await repo.CheckRangeAsync(range, ct: ct)));
    }

    /// <summary>Zasila pulę numerami z przedziału. Numery już obecne są pomijane (Skipped).</summary>
    [HttpPost("ranges/import")]
    [ProducesResponseType(typeof(RangeImportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RangeImportResponse>> ImportRange([FromBody] RangeRequest request, CancellationToken ct)
    {
        var range = request.ToRange();
        var result = await repo.ImportRangeAsync(range, Editor(), ct);

        return Ok(new RangeImportResponse(
            range.Type, range.FirstFullNumber, range.LastFullNumber,
            result.Requested, result.Added, result.Skipped));
    }

    // ------------------------------------------------------------ zmiana stanu

    /// <summary>
    /// Zmiana stanu numeru: Reserve (Dostępny -> Zarezerwowany), Use (Dostępny/Zarezerwowany -> Użyty),
    /// Release (Zarezerwowany -> Dostępny), Cancel (Dostępny/Zarezerwowany -> Anulowany).
    /// 409 gdy przejście jest niedozwolone, 404 gdy numeru nie ma w puli.
    /// </summary>
    [HttpPost("{fullNumber}/state")]
    [ProducesResponseType(typeof(RegisteredNumberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegisteredNumberDto>> ChangeState(string fullNumber, [FromBody] ChangeStateRequest request, CancellationToken ct)
    {
        var editor = Editor();
        var entity = request.Action switch
        {
            StateAction.Reserve => await repo.ReserveAsync(fullNumber, editor, ct),
            StateAction.Use => await repo.MarkUsedAsync(fullNumber, editor, ct),
            StateAction.Release => await repo.ReleaseAsync(fullNumber, editor, ct),
            StateAction.Cancel => await repo.CancelAsync(fullNumber, editor, ct),
            _ => throw new ArgumentException($"Nieznana akcja {request.Action}.")
        };

        return Ok(RegisteredNumberDto.From(entity));
    }

    /// <summary>Pobiera i rezerwuje kolejny wolny numer danego typu. 409 gdy pula jest pusta.</summary>
    [HttpPost("acquire")]
    [ProducesResponseType(typeof(RegisteredNumberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegisteredNumberDto>> Acquire([FromBody] AcquireRequest request, CancellationToken ct)
        => Ok(RegisteredNumberDto.From(await repo.AcquireNextAsync(request.Type, Editor(), ct)));

    // ------------------------------------------------------------ pomocnicze

    private string Editor()
    {
        var header = Request.Headers[EditorHeader].FirstOrDefault();
        var value = string.IsNullOrWhiteSpace(header) ? User.Identity?.Name : header.Trim();
        return string.IsNullOrWhiteSpace(value) ? DefaultEditor : value[..Math.Min(value.Length, 256)];
    }
}
