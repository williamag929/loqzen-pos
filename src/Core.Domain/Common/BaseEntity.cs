using System;

namespace Core.Domain.Common;

public abstract class BaseEntity
{
    /// <summary>Settable so a row replicated from another terminal keeps its original id (catalog snapshot, sync inbox).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
