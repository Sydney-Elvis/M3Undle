# Groups File Format and Validation

## Overview
The groups file is a text-based configuration file used to specify which IPTV channel groups to keep or drop during playlist filtering.

## File Format

### Header
Every groups file must contain the following header lines:

```
######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  New groups are marked with '##' for easy identification.                ######
######  Created with iptv version 0.40                                          ######
```

All header lines are exactly 88 characters wide with aligned trailing `######`.

### Version Tracking
Starting with version 1.0, groups files include a version line in the header. This tracks:
- **Major version** (X): Incompatible changes
- **Minor version** (Y): Compatible changes

The tool will:
- **Allow** files created with the same major version (e.g., 1.0 and 1.5 are compatible)
- **Reject** files created with a different major version (e.g., 1.0 and 2.0 are incompatible)
- **Warn** but create a backup when encountering an invalid or missing header

### Group Lines
After the header, each line represents a channel group:

```
#Sports          # Keep this group (commented with #)
News             # Drop this group (not commented)
#Entertainment   # Keep this group
Movies           # Drop this group
##Documentary    # Newly added group (marked with ##)
```

**Note**: Groups marked with `##` are newly discovered groups that were automatically added by the tool. You can change them to a single `#` to keep them, or remove the `##` entirely to drop them.

## File Validation

### Valid File Criteria
A file is considered valid if it:
1. Contains at least one of the required header lines
2. Has a version that matches the current major version (if version is present)

### Invalid File Handling
When an invalid file is detected:
1. A warning is displayed with the specific issue
2. The file is NOT modified
3. The command exits with an error code
4. Use `--force` to override validation and modify the file anyway

Example warning:
```
Warning: File was created with iptv version 2.0 but current version is 0.40 (major version mismatch)
The file will NOT be modified.
Use --force to override this check.
```

With `--force`:
```
Warning: File was created with iptv version 2.0 but current version is 0.40 (major version mismatch)
Proceeding due to --force flag.
Added 3 new group(s) to groups.txt
Backup saved to: groups.txt.bak
```

## Backup Files

### When Backups Are Created
Backups are created:
- **Only when changes are made** to an existing groups file (when new groups are discovered)
- **After validation passes** (or with `--force`)
- **Never created** when:
  - Validation fails without `--force`
  - No new groups are found (file unchanged)
  - Creating a new file (no existing file to back up)

### Backup Naming
- First backup: `<filename>.bak`
- Subsequent backups: `<filename>.bak1`, `<filename>.bak2`, etc.

### Backup Behavior
- If new groups are found, the backup is created and kept
- If no new groups are found, no backup is created and a message confirms the file is unchanged

## Usage Examples

### Creating a New Groups File
```bash
iptv groups --playlist-url https://provider.com/playlist.m3u --out-groups groups.txt
```

Creates `groups.txt` with:
- Header including current version
- All discovered groups (without # prefix)

### Updating an Existing Groups File
```bash
iptv groups --playlist-url https://provider.com/playlist.m3u --out-groups groups.txt
```

If `groups.txt` exists:
1. Validates the file format and version
2. Creates a backup at `groups.txt.bak`
3. Adds new groups with # prefix (marked as KEEP)
4. Preserves existing groups and their keep/drop status
5. Displays list of new groups found

Output:
```
Added 3 new group(s) to groups.txt
Backup saved to: groups.txt.bak
New groups found:
  Documentary
  Kids
  International
```

The file will contain:
```
######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  New groups are marked with '##' for easy identification.                ######
######  Created with iptv version 1.0 ######

#Sports
News
##Documentary
##Kids
##International
```

### No New Groups
```bash
iptv groups --playlist-url https://provider.com/playlist.m3u --out-groups groups.txt
```

Output:
```
No new groups found. File groups.txt unchanged.
```

(Backup is automatically deleted)

## Version Migration

### Upgrading from Pre-1.0 Files
If you have a groups file without a version line:
- The tool will add the version line automatically when you update the file
- Your existing group selections are preserved
- A backup is created for safety

### Version Updates
When the tool detects a different version in your groups file:
- If the major version is the same, the version line is automatically updated to the current version
- A backup is created before updating
- The tool displays a message indicating the version was updated (e.g., "Updated version from 1.0 to 1.2")
- With `--force`, version updates will occur even if validation would normally fail

### Major Version Incompatibility
If you need to use a groups file from a different major version:
1. Without `--force`: The tool will refuse to modify it
2. With `--force`: The tool will update the version line and proceed
3. Alternatively, create a new groups file from scratch

## Best Practices

1. **Version Control**: Keep your groups file in version control (git)
2. **Backups**: The tool creates automatic backups, but keep your own as well
3. **Review Changes**: After updating, review the file to ensure new groups are categorized correctly
4. **Regular Updates**: Run the groups command periodically to discover new channels
5. **Edit Carefully**: When manually editing, preserve the header format exactly

## Troubleshooting

### "File does not appear to be a valid groups file"
**Cause**: The file is missing the required header lines.

**Solution**: 
- If it's a valid groups file, ensure the header lines are present
- If starting fresh, delete the file and let the tool create a new one

### "Major version mismatch"
**Cause**: The file was created with a different major version of iptv.

**Solution**:
- Create a new groups file with the current version
- Or manually update the version line (may cause issues)

### "Too many backup files exist"
**Cause**: More than 999 backup files already exist.

**Solution**: Clean up old `.bak` and `.bakN` files in the directory.
