using Core.Domain.Common;

namespace Core.Domain.Entities;

public class Customer : BaseEntity
{
    public string DocumentId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Tax : BaseEntity
{
    public string Name { get; set; } = "IVA";
    public decimal Rate { get; set; } = 0.19m; // 19%
    public bool IsActive { get; set; } = true;
}
