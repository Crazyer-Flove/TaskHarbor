# Publish to GitHub

The upstream public repository is [Crazyer-Flove/TaskHarbor](https://github.com/Crazyer-Flove/TaskHarbor), licensed under MIT. Source, documentation and first-party assets are tracked; personal task data, configuration, build caches and executables are excluded. Fork or clone upstream for normal development. The following steps are for publishing a separate copy from a source archive.

1. Extract `TaskHarbor-3.0.0-source.zip` into a new folder.
2. Read `README.md`, `LICENSE`, `THIRD_PARTY_NOTICES.md` and `docs/VERIFICATION.md`.
3. Build and test. Manually verify the desktop UI before calling a public release stable.
4. Create an empty repository on your GitHub account (for example `TaskHarbor`). Do not initialize a second README remotely when you already have local files.
5. Run the following **inside the extracted TaskHarbor directory**:

```powershell
git init -b main
git add .
git status --short
git commit -m "Initial TaskHarbor release"
git remote add origin YOUR_GITHUB_REPOSITORY_URL
git push -u origin main
```

Replace `YOUR_GITHUB_REPOSITORY_URL` with the exact HTTPS or SSH URL of your own repository. Git may require your existing name/email and GitHub authentication. Do not put credentials in commands or commit them to the project.

Only source/docs/assets should appear in `git status`. `build/`, `dist/`, task data, keys, runtime profiles and SDK caches are ignored. Do not force-add ignored files.

The provided GitHub Actions workflow restores the SDK, runs tests, builds and uploads an artifact on Windows. It does not publish a GitHub Release automatically. Once CI and manual checks pass, create a version tag and a Release using the Windows archive and checksums from `dist/`.

Set your repository description, topics, issue policies and private vulnerability reporting as appropriate. Add real screenshots with fictional tasks only after validating the running application; do not label concept images as product screenshots.
