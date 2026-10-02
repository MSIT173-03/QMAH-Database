using Microsoft.EntityFrameworkCore;
using QMAH.Infrastructure.Models.Entities;
using QMAH.Infrastructure.Models.Identity;

namespace QMAH.Infrastructure.Data;

public partial class QmahDbContext
{
    public DbSet<ArtifactAppreciationVote> ArtifactAppreciationVotes { get; set; }

    private static void ConfigureAppreciation(ModelBuilder builder)
    {
        builder.Entity<ArtifactAppreciationVote>(entity => {
            entity.ToTable("ArtifactAppreciationVotes", "catalog");
            entity.HasKey(item => new { item.AnswerId, item.UserId });
            entity.Property(item => item.CreatedAt).HasPrecision(3);
            entity.HasOne(item => item.Answer).WithMany().HasForeignKey(item => item.AnswerId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}
