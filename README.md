# HoverTrailer

<div align="center">
    <p>
        <img alt="HoverTrailer Logo" src="logo/logo.png" width="450"/>
    </p>
    <p>
        A modern Netflix-style hover trailer preview plugin for Jellyfin that displays movie trailers on hover.
    </p>

[![Build](https://github.com/Fovty/HoverTrailer/actions/workflows/build.yml/badge.svg)](https://github.com/Fovty/HoverTrailer/actions/workflows/build.yml)
[![CodeQL](https://github.com/Fovty/HoverTrailer/actions/workflows/codeql.yml/badge.svg)](https://github.com/Fovty/HoverTrailer/actions/workflows/codeql.yml)
<a href="https://github.com/Fovty/HoverTrailer/releases">
<img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/Fovty/HoverTrailer/total?label=github%20downloads"/>
</a>
</div>

## Manifest URL

```
https://raw.githubusercontent.com/Fovty/HoverTrailer/master/manifest.json
```

## Features

- **Netflix-style Hover Previews**: Display movie trailers when hovering over movie cards
- **Customizable Positioning**: Adjust horizontal and vertical offset of trailer previews
- **Flexible Sizing Options**:
  - Fit to Video Content (automatic sizing based on video aspect ratio)
  - Manual Width/Height settings
- **Visual Customization**:
  - Adjustable opacity (0.1 to 1.0)
  - Configurable border radius (0-50px)
  - Percentage-based scaling (50% to 1500%)
- **Audio Control**: Optional audio playback for trailer previews
- **Multi-source Trailer Detection**:
  - Local trailer files
  - Remote YouTube trailers via Jellyfin's native API
- **Debug Mode**: Comprehensive logging for troubleshooting

## Installation

### From Jellyfin Plugin Catalog (Recommended)
1. Open **Jellyfin Admin Dashboard**
2. Navigate to **Plugins** → **Manage Repositories**
3. Click **+ (Add)** to add a new repository
4. Enter the **Manifest URL**:
   ```
   https://raw.githubusercontent.com/Fovty/HoverTrailer/master/manifest.json
   ```
5. Click **Save**
6. Navigate back to **Plugins**
7. Search for **"HoverTrailer"**
8. Click **Install**
9. **Restart Jellyfin server** to activate the plugin

### Manual Installation
1. Download the latest release from [GitHub Releases](../../releases)
2. Extract the `.dll` file to your Jellyfin plugins directory:
   - **Windows**: `%ProgramData%\Jellyfin\Server\plugins\HoverTrailer`
   - **Linux**: `/var/lib/jellyfin/plugins/HoverTrailer`
   - **Docker**: `/config/plugins/HoverTrailer`
3. Restart Jellyfin server

## Configuration

Access plugin settings through:
**Jellyfin Admin Dashboard** → **Plugins** → **HoverTrailer** → **Settings**

### Hover Preview Customization

| Setting | Description | Default | Range |
|---------|-------------|---------|-------|
| **Horizontal Offset** | X-axis position adjustment | 0px | -2000 to +2000px |
| **Vertical Offset** | Y-axis position adjustment | 0px | -2000 to +2000px |
| **Sizing Mode** | How to size the preview | Fit to Video Content | Fixed/Fit to Content |
| **Width** | Manual width (Fixed mode) | 400px | 100-2000px |
| **Height** | Manual height (Fixed mode) | 225px | 100-2000px |
| **Content Scale** | Percentage scaling (Fit mode) | 200% | 50-1500% |
| **Preview Opacity** | Transparency level | 1.0 | 0.1-1.0 |
| **Border Radius** | Corner rounding | 10px | 0-50px |

### Audio & Debug Settings

| Setting | Description | Default |
|---------|-------------|---------|
| **Enable Preview Audio** | Play audio with trailers | ✅ Enabled |
| **Enable Debug Mode** | Detailed console logging | ✅ Enabled |

## Troubleshooting

### Trailers Not Playing
1. **Check trailer availability**:
   - Verify movie has trailer metadata in Jellyfin
   - Check if trailer files exist in media directory

2. **Enable debug mode**:
   - Turn on "Enable Debug Mode" in plugin settings
   - Check browser console for error messages

3. **Audio issues**:
   - Browser may block autoplay with audio
   - Plugin automatically falls back to muted playback

### Preview Not Showing
1. **Verify plugin installation**:
   - Check plugin appears in Jellyfin admin panel
   - Ensure plugin is enabled and active

2. **Clear browser cache**:
   - Force refresh browser (Ctrl+F5)
   - Clear Jellyfin web client cache

### Docker Permission Issues
If you encounter `Access to the path '/usr/share/jellyfin/web/index.html' is denied` or similar permission errors in Docker:

**Option 1: Use File Transformation Plugin (Recommended)**

HoverTrailer now automatically detects and uses the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin (v2.2.1.0+) if it's installed. This eliminates permission issues by transforming content at runtime without modifying files on disk.

**Installation Steps:**
1. Install the File Transformation plugin from the Jellyfin plugin catalog
2. Restart Jellyfin
3. HoverTrailer will automatically detect and use it (no configuration needed)
4. Check logs to confirm: Look for "Successfully registered transformation with File Transformation plugin"

**Benefits:**
- No file permission issues in Docker environments
- Works with read-only web directories
- Survives Jellyfin updates without re-injection
- No manual file modifications required

**Option 2: Fix File Permissions**
```bash
# Find the actual index.html location
docker exec -it jellyfin find / -name index.html

# Fix ownership (replace 'jellyfin' with your container name and adjust user:group if needed)
docker exec -it --user root jellyfin chown jellyfin:jellyfin /jellyfin/jellyfin-web/index.html

# Restart container
docker restart jellyfin
```

**Option 3: Manual Volume Mapping**
```bash
# Extract index.html from container
docker cp jellyfin:/jellyfin/jellyfin-web/index.html /path/to/jellyfin/config/index.html

# Add to docker-compose.yml volumes section:
volumes:
  - /path/to/jellyfin/config/index.html:/jellyfin/jellyfin-web/index.html
```

**How It Works:**
HoverTrailer uses reflection-based integration to detect File Transformation at runtime. If the plugin is available, HoverTrailer registers a transformation handler that injects the script tag into index.html as it's served to browsers. If File Transformation isn't available, HoverTrailer falls back to direct file modification (which may require permissions as described in Options 2-3).

### Performance Issues
1. **Adjust sizing settings**:
   - Lower "Content Scale" percentage
   - Use "Fixed" sizing mode for consistent performance

2. **Network considerations**:
   - Remote trailers require internet connectivity
   - Local trailers provide better performance

## Development

### Building from Source
```bash
# Clone repository
git clone https://github.com/Fovty/HoverTrailer.git
cd hovertrailer

# Build plugin (warnings suppressed due to StyleCop/Code Analysis)
dotnet build --configuration Release --property:TreatWarningsAsErrors=false
```

### Project Structure
```
Fovty.Plugin.HoverTrailer/
├── Api/                    # API controllers
├── Configuration/          # Plugin configuration
├── Exceptions/            # Custom exceptions
├── Helpers/               # Utility classes
└── Models/                # Data models
```

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Guidelines
- Follow existing code style and conventions
- Update documentation for configuration changes
- Test across different browsers and Jellyfin versions

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- **Jellyfin Team** - For the excellent media server platform
- **IntroSkipper Plugin** - Architecture and CI/CD inspiration
- **Jellyscrub Plugin** - Configuration UI patterns

## Support

- **Issues**: [GitHub Issues](../../issues)
- **Discussions**: [GitHub Discussions](../../discussions)
- **Jellyfin Community**: [Official Jellyfin Forums](https://forum.jellyfin.org/)

---

**Note**: This plugin is not officially affiliated with Jellyfin or Netflix. It's a community-developed enhancement for the Jellyfin media server.
