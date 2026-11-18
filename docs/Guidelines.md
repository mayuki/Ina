## Repository layout
Projects live in `src` and `tests` under the repository root.

- repository root
	- .gitignore
	- Ina.slnx
	- global.json
	- Directory.Build.props / Directory.Packages.props
	- dotnet-tools.json
	- eng (engineering scripts, e.g. regenerating the C library import tables)
	- docs
	- samples (applications used as end-to-end test subjects)
	- src
		- Ina (the `ina` command line tool)
		- Ina.Core (the build pipeline: packs, ILCompiler driver, link step)
		- Ina.Linker (the managed ELF linker)
	- tests
		- Ina.Linker.Tests
		- Ina.Core.Tests

## Project rules
- Keep the tree in a state where it always builds, runs and can be checked
- Keep the docs up to date so the developer can follow the progress
- .NET CLI tools must not be installed globally
- Commit messages follow the conventional commit format
- Dependencies must be licensed under MIT, Apache 2.0, BSD, ISC, MPL or be in the
  public domain
  - Exceptions may be granted for proprietary licenses such as the Windows SDK,
    or for LGPL packages that come in as a direct or transitive dependency of a
    development tool. Ask the user whenever one of those is needed
- Ina must not require a native toolchain (compiler, assembler, linker, sysroot)
  on the machine that runs it. Native tools may only be used as development aids,
  never by the product or by its tests

## Documentation
- When the implementation and the docs disagree, write the change back into the
  docs
- Before committing, review whether anything new needs to be specified and write
  it into the docs

## Coding rules
- Practice test-driven development (always Red → Green)
- Code says How, tests say What, commit logs say Why, and code comments say Why not
- Always confirm that the build and the tests pass after a change
  - This may be skipped for docs-only changes
- Write code comments in English
- Keep code readable by using blank lines and comments appropriately.
- Run the tests with `dotnet test` from the repository root (Microsoft.Testing.Platform
  is selected in global.json)
