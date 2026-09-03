using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class ArtifactCategory
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public virtual ICollection<Artifact> Artifacts { get; set; } = new List<Artifact>();

    public virtual ICollection<KeyDefinition> KeyDefinitions { get; set; } = new List<KeyDefinition>();
}