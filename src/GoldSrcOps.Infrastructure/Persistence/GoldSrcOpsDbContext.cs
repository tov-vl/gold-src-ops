using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence;

public sealed class GoldSrcOpsDbContext : DbContext
{
    public GoldSrcOpsDbContext(DbContextOptions<GoldSrcOpsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Server> Servers => Set<Server>();

    public DbSet<ServerCurrentState> ServerCurrentStates => Set<ServerCurrentState>();

    public DbSet<PollSnapshot> PollSnapshots => Set<PollSnapshot>();

    public DbSet<AvailabilityIncident> AvailabilityIncidents => Set<AvailabilityIncident>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("goldsrcops");

        modelBuilder.Entity<Server>(server =>
        {
            server.ToTable("servers");
            server.HasKey(x => x.Id);
            server.Property(x => x.Name).HasMaxLength(200).IsRequired();
            server.Property(x => x.Game).HasConversion<string>().HasMaxLength(32).IsRequired();
            server.Property(x => x.Notes).HasMaxLength(2000);
            server.Property(x => x.CreatedAtUtc).IsRequired();

            server.OwnsOne(x => x.Endpoint, endpoint =>
            {
                endpoint.Property(x => x.Host).HasColumnName("host").HasMaxLength(255).IsRequired();
                endpoint.Property(x => x.QueryPort).HasColumnName("query_port").IsRequired();
                endpoint.Property(x => x.RconPort).HasColumnName("rcon_port");
            });

            server.HasOne(x => x.CurrentState)
                .WithOne(x => x.Server)
                .HasForeignKey<ServerCurrentState>(x => x.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServerCurrentState>(state =>
        {
            state.ToTable("server_current_states");
            state.HasKey(x => x.ServerId);
            state.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            state.Property(x => x.CurrentMap).HasMaxLength(128);
            state.Property(x => x.FailureReason).HasMaxLength(2000);
            state.Property(x => x.ConsecutiveFailures).IsRequired();
        });

        modelBuilder.Entity<PollSnapshot>(snapshot =>
        {
            snapshot.ToTable("poll_snapshots");
            snapshot.HasKey(x => x.Id);
            snapshot.Property(x => x.Map).HasMaxLength(128);
            snapshot.Property(x => x.RawVersion).HasMaxLength(128);
            snapshot.Property(x => x.FailureReason).HasMaxLength(2000);
            snapshot.HasIndex(x => new { x.ServerId, x.CheckedAtUtc });
            snapshot.HasOne(x => x.Server)
                .WithMany()
                .HasForeignKey(x => x.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AvailabilityIncident>(incident =>
        {
            incident.ToTable("availability_incidents");
            incident.HasKey(x => x.Id);
            incident.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
            incident.Property(x => x.StartReason).HasMaxLength(2000).IsRequired();
            incident.Property(x => x.EndReason).HasMaxLength(2000);
            incident.HasIndex(x => new { x.ServerId, x.ClosedAtUtc });
            incident.Ignore(x => x.IsOpen);
            incident.HasOne(x => x.Server)
                .WithMany()
                .HasForeignKey(x => x.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
