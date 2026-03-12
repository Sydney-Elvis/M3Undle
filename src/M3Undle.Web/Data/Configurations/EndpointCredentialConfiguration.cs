using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace M3Undle.Web.Data.Configurations;

public sealed class EndpointCredentialConfiguration : IEntityTypeConfiguration<EndpointCredential>
{
    public void Configure(EntityTypeBuilder<EndpointCredential> builder)
    {
        builder.ToTable("endpoint_credentials");

        builder.HasKey(x => x.EndpointCredentialId);
        builder.Property(x => x.EndpointCredentialId).HasColumnName("endpoint_credential_id");
        builder.Property(x => x.Username).HasColumnName("username").IsRequired();
        builder.Property(x => x.NormalizedUsername).HasColumnName("normalized_username").IsRequired();
        builder.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(x => x.AuthType).HasColumnName("auth_type").IsRequired();
        builder.Property(x => x.CreatedUtc).HasColumnName("created_utc").IsRequired();
        builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc").IsRequired();

        builder.HasIndex(x => x.Username).IsUnique();
        builder.HasIndex(x => x.NormalizedUsername).IsUnique();
    }
}
