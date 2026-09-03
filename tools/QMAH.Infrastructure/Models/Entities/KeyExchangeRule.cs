namespace QMAH.Infrastructure.Models.Entities;

/// <summary>
/// 定義鑰匙之間的兌換比例；規則本身不保存會員餘額。
/// </summary>
public partial class KeyExchangeRule
{
    public Guid Id { get; set; }

    public Guid SourceKeyDefinitionId { get; set; }

    public int SourceAmount { get; set; }

    public Guid TargetKeyDefinitionId { get; set; }

    public int TargetAmount { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual KeyDefinition SourceKeyDefinition { get; set; } = null!;

    public virtual KeyDefinition TargetKeyDefinition { get; set; } = null!;
}
