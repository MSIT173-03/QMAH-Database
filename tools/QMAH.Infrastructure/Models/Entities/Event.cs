using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class Event
{
    public Guid Id { get; set; }

    public string EventType { get; set; } = null!;

    public Guid? OrganizerUserId { get; set; }

    public string Title { get; set; } = null!;

    public string Content { get; set; } = null!;

    public string? Location { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public DateTime StartAt { get; set; }

    public DateTime EndAt { get; set; }

    public DateTime? RegistrationEndAt { get; set; }

    public int? Capacity { get; set; }

    public string ReviewStatus { get; set; } = null!;

    public string PublishStatus { get; set; } = null!;

    public string? ReviewNote { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<EventRegistration> EventRegistrations { get; set; } = new List<EventRegistration>();

    public virtual ICollection<CommunityRewardCampaign> RewardCampaigns { get; set; } = new List<CommunityRewardCampaign>();

    public virtual SocialPost? SocialPost { get; set; }
}
