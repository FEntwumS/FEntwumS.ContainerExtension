# Pull Request

## Describe your changes

Please include a summary of the change and which issue is fixed. Please also include relevant motivation and context.

## Issue ticket number and link

Fixes #xxx

## Type of change

- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Documentation update
- [ ] Security hardening / Infrastructure update

## Verification & Hardening Checklist

- [ ] I have performed a self-review of my code.
- [ ] I have verified that the code compiles with zero warnings (`dotnet build -c Release -warnaserror`).
- [ ] I have successfully run `dotnet test` locally and all tests pass.
- [ ] I have ensured thread-safe lifecycle management and absence of memory/concurrency leaks.
- [ ] I have ensured `Console.Write` leaks are absent in the production build.
