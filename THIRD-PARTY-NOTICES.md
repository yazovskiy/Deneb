# Third-party notices

Deneb is MIT-licensed. Bundled dependencies retain their own notices and licenses.

| Component | Version | Notices |
|---|---|---|
| Terminal.Gui | 1.19.0 | [MIT license](docs/licenses/Terminal.Gui.txt) from package source commit 79e2d14b68f07ed38c8f1f71aca1af7ab16d8d39 |
| NStack.Core | 1.1.1 | NuGet declares MIT; its source LICENSE.md also contains the [BSD notice](docs/licenses/NStack.txt) reproduced here from ce985f39994ac88fe410c3ef776a96ced04764cd |
| .NET runtime | 10.0.8 | MIT and third-party notices, copied from the runtime package into the release archive |
| System.Management / System.CodeDom | 9.0.4 | MIT and third-party notices, copied from their packages into the release archive |

The release archive includes `licenses/` with complete runtime and dependency notices. Build and test dependencies are listed separately in the committed NuGet lockfiles and are not shipped as development tools in the release.
