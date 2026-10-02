using Microsoft.EntityFrameworkCore;
using QMAH.Infrastructure.Models.Entities;

namespace QMAH.Infrastructure.Data;

public partial class QmahDbContext
{
    // 版本工具使用與產品相同的欄位精度，避免匯出及展示資料更新捨去小數進度。
    private static void ConfigureGameRewardContract(ModelBuilder builder)
    {
        builder.Entity<GameRoom>().Property(item => item.IsShowcase).HasDefaultValue(false, "DF_GameRooms_IsShowcase");
        builder.Entity<GamePlayer>().Property(item => item.RewardClaimedAt).HasPrecision(3);
        builder.Entity<GamePlayer>().Property(item => item.RewardKeyProgress).HasPrecision(12, 2);
        builder.Entity<MiniGameAttempt>().Property(item => item.KeyProgressReward).HasPrecision(12, 2);
        builder.Entity<MiniGameAttempt>().Property(item => item.KeyRewardDivisor).HasDefaultValue((byte)1, "DF_MiniGameAttempts_KeyRewardDivisor");
        builder.Entity<MiniGameAttempt>().Property(item => item.ConvertedNormalKeys).HasDefaultValue(0, "DF_MiniGameAttempts_ConvertedNormalKeys");
        builder.Entity<KeyProgressBalance>().Property(item => item.Balance).HasPrecision(12, 2);
        builder.Entity<KeyProgressTransaction>().Property(item => item.Amount).HasPrecision(12, 2);
    }
}
