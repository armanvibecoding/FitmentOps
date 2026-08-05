namespace AutoPartsStore.API.Models;

public static class OrderStatuses
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Shipped = "Shipped";
    public const string Delivered = "Delivered";
    public const string Cancelled = "Cancelled";
}

public static class PaymentStatuses
{
    public const string Pending = "Pending";
    public const string Paid = "Paid";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string PartiallyRefunded = "PartiallyRefunded";
    public const string Refunded = "Refunded";
}

public static class PaymentMethods
{
    public const string PayAtDelivery = "PayAtDelivery";
    public const string Marketplace = "Marketplace";
}

public static class PaymentProviders
{
    public const string Manual = "Manual";
}
