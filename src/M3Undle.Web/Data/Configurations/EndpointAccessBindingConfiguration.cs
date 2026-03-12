using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class EndpointAccessBindingConfiguration : IEntityTypeConfiguration<EndpointAccessBinding>
{
    public void Configure(EntityTypeBuilder<EndpointAccessBinding> builder)
    {
        builder.ToTable("endpoint_access_bindings");

        builder.HasKey(x => x.EndpointAccessBindingId);
        builder.Property(x => x.EndpointAccessBindingId).HasColumnName("endpoint_access_binding_id");
        builder.Property(x => x.EndpointCredentialId).HasColumnName("endpoint_credential_id").IsRequired();
        builder.Property(x => x.ActiveProfileId).HasColumnName("active_profile_id");
        builder.Property(x => x.DefaultProfileId).HasColumnName("default_profile_id");
        builder.Property(x => x.VirtualTunerId).HasColumnName("virtual_tuner_id");
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => x.EndpointCredentialId)
            .IsUnique()
            .HasDatabaseName("idx_endpoint_access_bindings_credential");

        builder.HasIndex(x => x.ActiveProfileId)
            .HasDatabaseName("idx_endpoint_access_bindings_active_profile");

        builder.HasOne(x => x.Credential)
            .WithMany(x => x.Bindings)
            .HasForeignKey(x => x.EndpointCredentialId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ActiveProfile)
            .WithMany(x => x.ActiveEndpointAccessBindings)
            .HasForeignKey(x => x.ActiveProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.DefaultProfile)
            .WithMany(x => x.DefaultEndpointAccessBindings)
            .HasForeignKey(x => x.DefaultProfileId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
