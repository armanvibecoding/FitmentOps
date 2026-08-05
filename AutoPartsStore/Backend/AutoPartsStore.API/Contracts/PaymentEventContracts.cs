using AutoPartsStore.API.Models;

namespace AutoPartsStore.API.Contracts;

public enum PaymentEventRegistrationOutcome
{
    Registered,
    Replayed,
    Conflict,
    InvalidRequest
}

public sealed record PaymentEventRegistrationResult(
    PaymentEventRegistrationOutcome Outcome,
    PaymentEvent? PaymentEvent = null,
    string? Message = null);
