namespace Purgely.Core.Startup;

/// <summary>A known startup entry: what it is and whether it is safe to switch off.</summary>
public sealed record KnownEntry(string[] Patterns, string Description, StartupSafety Safety, bool Heavy = false);

/// <summary>"What is this?" – a small built-in knowledge base of common startup entries.</summary>
public static class StartupKnowledge
{
    private static readonly KnownEntry[] Entries =
    {
        // Accessibility – must come first ("zoomtext" is not Zoom)
        new(new[] { "zoomtext", "jaws", "nvda", "narrator", "magnifier", "dragon", "windoweyes", "supernova" },
            "Accessibility tool (screen reader, magnifier or speech) – someone may depend on it.", StartupSafety.Keep),

        // Windows itself
        new(new[] { "securityhealth", "securityhealthsystray", "windowsdefender", "msmpeng", "msascui" },
            "Windows Security – shows protection status and warnings.", StartupSafety.Keep),

        // Antivirus & security – keep on
        new(new[] { "avira", "trendmicro", "fsecure", "gdata", "webroot", "panda", "comodo", "emsisoft", "hitmanpro", "zonealarm", "crowdstrike", "sentinel" },
            "Antivirus / security software.", StartupSafety.Keep),
        new(new[] { "avast", "avgui", "avgsvc", "kaspersky", "avpui", "esetgui", "egui", "ekrn", "bitdefender", "norton", "mcafee", "malwarebytes", "mbamtray", "sophos" },
            "Antivirus / security software.", StartupSafety.Keep),

        // Backup, VPN and device management – things stop silently without them
        new(new[] { "veeam", "acronis", "macrium", "reflect", "backblaze", "carbonite", "idrive", "crashplan", "aomei", "easeus", "synologydrive", "duplicati" },
            "Backup software – backups stop silently while it is off.", StartupSafety.Keep),
        new(new[] { "nordvpn", "expressvpn", "protonvpn", "openvpn", "wireguard", "globalprotect", "anyconnect", "forticlient", "pulsesecure", "surfshark", "mullvad", "tailscale", "zerotier" },
            "VPN – the secure connection works only while it runs.", StartupSafety.Keep),
        new(new[] { "intune", "omadm", "companyportal", "airwatch", "jamf", "kaseya", "ninjarmm", "datto", "connectwise" },
            "Company device management – your employer may require it.", StartupSafety.Keep),

        // Laptop hotkeys and keyboard/mouse
        new(new[] { "qaagent", "quickaccess", "acerquickaccess", "predatorsense", "armourycrate", "asusoptimization", "atkhotkey", "lenovovantage", "hotkey", "fnhotkey", "hpsysinfo", "hphotkey", "dellfeatures", "msicenter", "dragoncenter" },
            "Laptop hotkeys and fan/keyboard control – Fn keys or backlight may stop working.", StartupSafety.Keep),
        new(new[] { "logioptionsplus", "logioptions", "setpoint" }, "Logitech mouse/keyboard software – custom buttons stop working without it.", StartupSafety.Keep),

        // Hardware helpers
        new(new[] { "rtkaud", "ravcpl", "realtek" }, "Realtek audio – controls sound, microphone and audio jacks.", StartupSafety.Keep),
        new(new[] { "igfx", "intelgraphics", "intelgfx", "igcc" }, "Intel graphics helper (display settings, hotkeys).", StartupSafety.Keep),
        new(new[] { "synaptics", "syntp", "elantech", "etdctrl" }, "Touchpad driver – gestures may stop working without it.", StartupSafety.Keep),
        new(new[] { "nitrosense" }, "Acer NitroSense – fan and performance control.", StartupSafety.Keep),
        new(new[] { "nvbackend", "nvcontainer", "nvidiaapp", "nvtray", "nvidiashare" }, "NVIDIA graphics helper (NVIDIA app / GeForce overlay).", StartupSafety.Check),
        new(new[] { "radeonsoftware", "amdrsserv", "amdsoftware" }, "AMD graphics helper (Radeon Software).", StartupSafety.Check),
        new(new[] { "killer", "rivetnetworks" }, "Killer network suite – network priority tools; the network itself works without it.", StartupSafety.Check),
        new(new[] { "carecenter" }, "Acer Care Center – Acer updates and system checks.", StartupSafety.Check),
        new(new[] { "lghub", "logitech" }, "Logitech mouse/keyboard settings (custom buttons, lighting).", StartupSafety.Check),
        new(new[] { "razer", "synapse" }, "Razer device settings (custom buttons, lighting).", StartupSafety.Check),
        new(new[] { "icue", "corsair" }, "Corsair iCUE – device lighting and fan control.", StartupSafety.Check, Heavy: true),
        new(new[] { "msiafterburner", "rtss" }, "MSI Afterburner / RivaTuner – GPU overclocking and overlay.", StartupSafety.Check),

        // Cloud & sync – files stop syncing
        new(new[] { "onedrive" }, "OneDrive – files will not sync until you open OneDrive yourself.", StartupSafety.Check, Heavy: true),
        new(new[] { "dropbox" }, "Dropbox – files will not sync until you open Dropbox yourself.", StartupSafety.Check, Heavy: true),
        new(new[] { "googledrive" }, "Google Drive – files will not sync until you open it yourself.", StartupSafety.Check, Heavy: true),
        new(new[] { "ccxprocess", "creativecloud", "adobegcclient", "coresync" }, "Adobe Creative Cloud – sync and license checks.", StartupSafety.Check, Heavy: true),
        new(new[] { "bitwarden", "1password", "keepass", "lastpass" }, "Password manager – autofill works only while it runs.", StartupSafety.Check),
        new(new[] { "cloudflare", "warpsvc" }, "Cloudflare WARP / One – network protection works only while it runs.", StartupSafety.Check),
        new(new[] { "wallpaperengine" }, "Wallpaper Engine – animated wallpapers.", StartupSafety.Check, Heavy: true),

        // Game launchers & chat – safe
        new(new[] { "msteams", "teams" }, "Microsoft Teams.", StartupSafety.Safe, Heavy: true),
        new(new[] { "steam" }, "Steam – starts the game store in the background. Games still work when you open Steam yourself.", StartupSafety.Safe, Heavy: true),
        new(new[] { "epicgameslauncher", "epicgames" }, "Epic Games Launcher.", StartupSafety.Safe, Heavy: true),
        new(new[] { "battlenet" }, "Battle.net launcher.", StartupSafety.Safe, Heavy: true),
        new(new[] { "eadesktop", "eabackgroundservice", "originwebhelper" }, "EA app launcher.", StartupSafety.Safe, Heavy: true),
        new(new[] { "ubisoftconnect", "uplay" }, "Ubisoft Connect launcher.", StartupSafety.Safe, Heavy: true),
        new(new[] { "galaxyclient" }, "GOG Galaxy launcher.", StartupSafety.Safe, Heavy: true),
        new(new[] { "curseforge", "overwolf" }, "CurseForge / Overwolf – mods and in-game overlays.", StartupSafety.Safe, Heavy: true),
        new(new[] { "gameloop", "androws" }, "GameLoop – Android emulator.", StartupSafety.Safe, Heavy: true),
        new(new[] { "wago" }, "Wago App – WoW addon manager.", StartupSafety.Safe),
        new(new[] { "discord" }, "Discord – chat starts in the background.", StartupSafety.Safe, Heavy: true),
        new(new[] { "spotify" }, "Spotify – music player.", StartupSafety.Safe, Heavy: true),
        new(new[] { "slack" }, "Slack.", StartupSafety.Safe, Heavy: true),
        new(new[] { "zoom" }, "Zoom – opens when you join a meeting.", StartupSafety.Safe),
        new(new[] { "skype" }, "Skype.", StartupSafety.Safe),
        new(new[] { "whatsapp" }, "WhatsApp desktop.", StartupSafety.Safe),

        // Updaters & tools – safe
        new(new[] { "adobearm", "acrotray" }, "Adobe updater / tray helper – Adobe programs work without it.", StartupSafety.Safe),
        new(new[] { "jusched", "javaupdate" }, "Java updater.", StartupSafety.Safe),
        new(new[] { "ituneshelper" }, "iTunes helper – only needed to detect an iPhone automatically.", StartupSafety.Safe),
        new(new[] { "teamviewer", "anydesk", "rustdesk", "splashtop", "parsec", "chromeremotedesktop" },
            "Remote desktop tool – if someone connects to this PC without you starting it (remote support), keep it on.", StartupSafety.Check),
        new(new[] { "ccleaner" }, "CCleaner – background monitoring and offers.", StartupSafety.Safe),
        new(new[] { "driverbooster", "iobit", "smartgamebooster", "advancedsystemcare", "pcgameboost" }, "IObit tool – background scans and offers.", StartupSafety.Safe),
        new(new[] { "utorrent", "bittorrent" }, "µTorrent / BitTorrent – often shows ads; open it only when you need it.", StartupSafety.Safe),
        new(new[] { "qbittorrent" }, "qBittorrent.", StartupSafety.Safe),
        new(new[] { "daemontools", "dtlite", "dtshellhlp" }, "DAEMON Tools – virtual drives.", StartupSafety.Safe),
        new(new[] { "docker" }, "Docker Desktop.", StartupSafety.Safe, Heavy: true),
        new(new[] { "virtualbox", "vboxsvc" }, "VirtualBox.", StartupSafety.Safe),
        new(new[] { "githubdesktop" }, "GitHub Desktop.", StartupSafety.Safe),
    };

    public static KnownEntry? Find(string name, string target)
    {
        var hay = Keywords.Norm(name) + " " + Keywords.Norm(System.IO.Path.GetFileNameWithoutExtension(target));
        return Entries.FirstOrDefault(e => e.Patterns.Any(p => hay.Contains(p, StringComparison.OrdinalIgnoreCase)));
    }
}
