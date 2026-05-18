using AniRuntime.Core.Interfaces;

namespace AniRuntime.Loops;

/// <inheritdoc cref="IWithdrawalStateTracker"/>
public sealed class WithdrawalStateTracker : IWithdrawalStateTracker
{
    private DateTimeOffset? _expiresAt;

    public bool IsWithdrawn => _expiresAt.HasValue && DateTimeOffset.UtcNow < _expiresAt.Value;

    public void SetExpiry(DateTimeOffset? expiresAt) => _expiresAt = expiresAt;
}
