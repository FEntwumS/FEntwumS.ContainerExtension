# Pull Request

## Describe your changes

Please include a summary of the change and which issue is fixed.

## Issue ticket number and link

Fixes #xxx

## Type of change

- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Documentation update
- [ ] Security hardening / Infrastructure update

## Verification & Release Checklist

### 1. Code Quality & Standards
- [ ] I have performed a self-review of my code.
- [ ] I have verified that the code compiles with zero warnings (`dotnet build -c Release -warnaserror`).
- [ ] I have verified that NuGet dependency audits pass without High/Critical alerts.
- [ ] I have successfully run `dotnet test` locally and all tests pass.

### 2. Thread Safety & Concurrency
- [ ] I have verified that all UI modifications from background tasks/timers run on `Dispatcher.UIThread`.
- [ ] I have ensured thread-safe access to cached models using atomic locking patterns.
- [ ] I have utilized cancel-safe async logic and avoided finally-block cancellation leaks.

### 3. Memory & Resource Management
- [ ] I have checked for and eliminated potential memory leaks from event handler subscriptions.
- [ ] I have used weak references (`WeakReference<T>`) for transient/modal control tracking.
- [ ] I have verified that all rented arrays (e.g. `ArrayPool<T>`) are cleared and returned in `finally` blocks.

### 4. Nullity & Safety Guarding
- [ ] I have guarded against `NullReferenceException` on collection bounds and API results.
- [ ] I have verified timeout-safe regex evaluations.
- [ ] I have verified that file picker paths are checked for traversal and access safety.

### 5. Native AOT Compliance
- [ ] I have avoided using dynamic reflection or dynamic JSON deserialization that violates AOT compilation.

### 6. Security Sandboxing
- [ ] **Security:** If modifying Dockerfiles, I have ensured least-privilege execution (`USER oneware`).
- [ ] **Security:** I have verified no sensitive credentials or daemon sockets are leaked in telemetry or volume mounts.
