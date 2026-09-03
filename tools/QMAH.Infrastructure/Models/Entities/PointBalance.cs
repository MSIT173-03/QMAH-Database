using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class PointBalance
{
    public Guid UserId { get; set; }

    public int Balance { get; set; }

    public DateTime UpdatedAt { get; set; }
}