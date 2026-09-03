using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class Achievement
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public string? IconPath { get; set; }

    public string ConditionType { get; set; } = null!;

    public long ThresholdValue { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ICollection<UserAchievement> UserAchievements { get; set; } = new List<UserAchievement>();
}