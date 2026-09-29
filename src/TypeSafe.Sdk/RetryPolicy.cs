using System.Collections.ObjectModel;

namespace TypeSafeAI;

/// <summary>
/// Controls how the SDK retries a request that fails with a retryable error.
/// </summary>
/// <remarks>
/// <para>
/// The defaults reproduce the TypeSafe Python and JavaScript SDK defaults exactly, so behaviour
/// is identical across languages:
/// </para>
/// <list type="bullet">
///   <item><description>two retries after the initial attempt, so at most three attempts;</description></item>
///   <item><description><c>500 ms</c> initial backoff, doubled per attempt, capped at <c>5 s</c>;</description></item>
///   <item><description>up to <c>25%</c> of each delay removed at random as jitter;</description></item>
///   <item><description>retry on <c>408</c>, <c>429</c>, and every <c>5xx</c>;</description></item>
///   <item><description>honour <c>Retry-After</c> and <c>retry-after-ms</c> up to <c>60 s</c>;</description></item>
///   <item><description>a <c>30 s</c> budget that prevents retries whose delay would reach it.</description></item>
/// </list>
/// <para>
/// Retrying is safe for this API: <c>POST /v1/systemone</c> is a stateless evaluation call, and a
/// response that was never read is not billed to the caller.
/// </para>
/// </remarks>
public sealed class RetryPolicy
{
    private static readonly ReadOnlyCollection<int> DefaultStatuses = Array.AsReadOnly(
        [408, 429, .. Enumerable.Range(500, 100)]);

    private int _maxRetries = 2;
    private TimeSpan _backoffInitial = TimeSpan.FromMilliseconds(500);
    private TimeSpan _backoffMax = TimeSpan.FromSeconds(5);
    private double _backoffJitter = 0.25;
    private IReadOnlyList<int> _httpStatuses = DefaultStatuses;
    private bool _respectRetryAfter = true;
    private TimeSpan _maxRetryAfter = TimeSpan.FromSeconds(60);
    private bool _retryConnectionErrors = true;
    private bool _retryTimeoutErrors = true;
    private TimeSpan? _totalBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryPolicy"/> class with the documented
    /// defaults.
    /// </summary>
    public RetryPolicy()
    {
    }

    /// <summary>
    /// Gets a policy that never retries.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>new RetryPolicy { MaxRetries = 0 }</c>, and the documented way to turn
    /// retrying off.
    /// </remarks>
    public static RetryPolicy None { get; } = new() { MaxRetries = 0 };

    /// <summary>
    /// Gets a policy using the documented defaults.
    /// </summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>
    /// Gets the maximum number of retries after the initial attempt. Zero disables retrying.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaxRetries
    {
        get => _maxRetries;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxRetries = value;
        }
    }

    /// <summary>
    /// Gets the delay before the first retry. The delay doubles for each subsequent retry, up to
    /// <see cref="BackoffMax"/>. <see cref="TimeSpan.Zero"/> disables backoff.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan BackoffInitial
    {
        get => _backoffInitial;
        init
        {
            ThrowIfNegative(value);
            _backoffInitial = value;
        }
    }

    /// <summary>
    /// Gets the ceiling applied to the computed backoff delay.
    /// <see cref="TimeSpan.Zero"/> disables backoff.
    /// </summary>
    /// <remarks>
    /// Any retry delay is capped at <see cref="uint.MaxValue"/> minus one milliseconds, about 49.7
    /// days, the longest wait <see cref="Task.Delay(TimeSpan)"/> accepts.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan BackoffMax
    {
        get => _backoffMax;
        init
        {
            ThrowIfNegative(value);
            _backoffMax = value;
        }
    }

    /// <summary>
    /// Gets the fraction of each computed delay that is removed at random, between <c>0</c> and
    /// <c>1</c> inclusive. Jitter prevents many clients from retrying in lockstep after a shared
    /// outage.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside <c>0</c> to <c>1</c>.</exception>
    public double BackoffJitter
    {
        get => _backoffJitter;
        init
        {
            if (double.IsNaN(value) || value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "Backoff jitter must be between 0 and 1 inclusive.");
            }

            _backoffJitter = value;
        }
    }

    /// <summary>
    /// Gets the HTTP status codes that are retried. Defaults to <c>408</c>, <c>429</c>, and
    /// <c>500</c> through <c>599</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public IReadOnlyList<int> HttpStatuses
    {
        get => _httpStatuses;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _httpStatuses = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a <c>Retry-After</c> or <c>retry-after-ms</c> response
    /// header overrides the computed backoff delay.
    /// </summary>
    /// <remarks>
    /// Both the delta-seconds and the HTTP-date form of <c>Retry-After</c> are understood.
    /// <c>retry-after-ms</c> is checked first when present, matching the sibling SDKs.
    /// </remarks>
    public bool RespectRetryAfter
    {
        get => _respectRetryAfter;
        init => _respectRetryAfter = value;
    }

    /// <summary>
    /// Gets the longest server-requested delay the SDK will honour. A longer <c>Retry-After</c>
    /// is ignored and the computed backoff is used instead.
    /// </summary>
    /// <remarks>
    /// A server-requested delay that is honoured is still capped at about 49.7 days, the longest
    /// wait <see cref="Task.Delay(TimeSpan)"/> accepts, so <see cref="TimeSpan.MaxValue"/> is a safe
    /// way to always honour the server.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan MaxRetryAfter
    {
        get => _maxRetryAfter;
        init
        {
            ThrowIfNegative(value);
            _maxRetryAfter = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a <see cref="TypeSafeConnectionException"/> is retried.
    /// </summary>
    public bool RetryConnectionErrors
    {
        get => _retryConnectionErrors;
        init => _retryConnectionErrors = value;
    }

    /// <summary>
    /// Gets a value indicating whether a <see cref="TypeSafeTimeoutException"/> is retried.
    /// </summary>
    public bool RetryTimeoutErrors
    {
        get => _retryTimeoutErrors;
        init => _retryTimeoutErrors = value;
    }

    /// <summary>
    /// Gets the elapsed-time budget used to decide whether another retry may start.
    /// <see langword="null"/> removes the limit.
    /// </summary>
    /// <remarks>
    /// A retry whose delay would reach or exceed the remaining budget is not attempted; the last
    /// error is rethrown instead. An attempt already in progress is bounded by the per-attempt
    /// timeout, not by this budget, so total call duration can exceed this value.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan? TotalBudget
    {
        get => _totalBudget;
        init
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "The retry budget cannot be negative.");
            }

            _totalBudget = value;
        }
    }

    /// <summary>
    /// Gets an optional predicate consulted for every failure. Returning <see langword="true"/>
    /// retries the request, in addition to the built-in rules.
    /// </summary>
    /// <remarks>
    /// This is the escape hatch for retrying on application-specific conditions. It cannot
    /// suppress a retry that the built-in rules already allow.
    /// </remarks>
    public Func<Exception, bool>? ShouldRetry { get; init; }

    /// <summary>
    /// Gets an optional callback invoked just before each retry is attempted.
    /// </summary>
    /// <remarks>
    /// Intended for diagnostics. Use <see cref="TypeSafeClientOptions.LoggerFactory"/> for
    /// ordinary logging instead.
    /// </remarks>
    public Action<RetryAttempt>? OnRetry { get; init; }

    private static void ThrowIfNegative(TimeSpan value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value cannot be negative.");
        }
    }
}
