using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class UserKeyBalance
{
    public Guid UserId { get; set; }

    public Guid KeyDefinitionId { get; set; }

    public int Balance { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual KeyDefinition KeyDefinition { get; set; } = null!;
}