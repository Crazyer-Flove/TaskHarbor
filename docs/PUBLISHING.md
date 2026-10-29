# Publish to GitHub

The upstream public repository is [Crazyer-Flove/TaskHarbor](https://github.com/Crazyer-Flove/TaskHarbor), licensed under MIT. Source, documentation and first-party assets are tracked; personal task data, configuration, build caches and executables are excluded. Fork or clone upstream for normal development. The following steps are for publishing a separate copy from a source archive.

1. Extract `TaskHarbor-3.0.3-source.zip` into a new folder.
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

## Maintainer release policy

The owner requests that future source updates also update GitHub Releases. Treat source synchronization and the corresponding downloadable build as one delivery:

1. For application changes, bump the app version and update the changelog/readmes. Do not move an already published version tag to different application code.
2. Run relevant tests, build the Windows executable, run its isolated self-test and verify the manifest checksum. Document any remaining desktop acceptance gaps.
3. Commit and push the reviewed source, then check Windows CI. Never commit executables, personal task data, keys or SDK caches into Git.
4. Run `scripts/package.ps1` and prepare a versioned Release (`vMAJOR.MINOR.PATCH`) targeting the corresponding source commit.
5. Attach `TaskHarbor.exe`, the Windows ZIP, source ZIP, `release.json`, `SHA256SUMS.txt`, the WebView2 license and third-party notices. The Windows ZIP is the recommended download because it bundles notices.
6. Prepare as a draft, verify uploaded asset sizes and SHA-256 digests, then publish. Confirm the public Release and download links and report them to the owner.
7. Documentation-only updates that do not alter the executable should update source/documentation and the matching Release notes or documentation package, without claiming that a new application build was produced.

后续更新源码时，同时维护对应的 Releases 下载：功能变更升级版本，验证并打包 EXE／Windows 包／源码包／校验值与许可文件，源码与版本标签对应，上传检查通过后发布。仅文档变更更新对应说明，不虚构新的二进制版本。除非用户明确要求只改本地，否则不要只推送源码而遗漏下载版。
