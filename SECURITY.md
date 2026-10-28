# Security and privacy

Never put personal task exports, screenshots with private information, API keys or configuration files in public issues. Use the repository's private vulnerability reporting facility if enabled by its owner. If it is unavailable, open an issue requesting a private reporting channel without publishing vulnerability details or secrets.

API keys are encrypted with Windows current-user DPAPI. Task JSON is plaintext on the local filesystem. AI requests send only the text/images explicitly submitted to the configured endpoint. TaskHarbor does not provide encryption of exported task backups or cloud storage.

The application is currently unsigned. Keep WebView2 Runtime maintained by your normal Windows software-management process. Third-party endpoints, including custom AI services, are selected and trusted by the user.

Security fixes target the latest 3.x code line. Older versions may require upgrading to receive a fix.
