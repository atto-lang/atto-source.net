//! Source-only canonical Atto core identity gate. Not a compiler/guest test.
use sha1::{Digest, Sha1};
use std::collections::BTreeMap;
use std::env;
use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};

const SNAPSHOT: &str = "contracts/atto-core-v1";
const MAX_BYTES: usize = 1024 * 1024;
const MODULES: [(&str, &str); 3] = [
    ("numeric.atto", "5ced991f1d49b3c2a60fdd72b9003c0a4d18cd72"),
    ("collections.atto", "c5d6659dda078736cb170d5c1d59fc04e700d023"),
    ("core.atto", "d2cd9b8e0cd3e3bb3ee6a5f9cebf9ab3312c44f4"),
];

fn real_directory(path: &Path) -> Result<(), String> {
    let meta = fs::symlink_metadata(path).map_err(|_| "invalid snapshot directory")?;
    if !meta.is_dir() || meta.file_type().is_symlink() {
        return Err("invalid snapshot directory".into());
    }
    Ok(())
}

fn git_blob_hash(bytes: &[u8]) -> String {
    let mut sha = Sha1::new();
    sha.update(format!("blob {}\0", bytes.len()).as_bytes());
    sha.update(bytes);
    format!("{:x}", sha.finalize())
}

// Unix source CI must not dereference a symlink swapped into the final
// module filename after the directory listing. A full hostile-concurrency
// proof for ancestor directory replacement remains outside this gate.
fn open_module(path: &Path) -> Result<(File, fs::Metadata), String> {
    let initial = fs::symlink_metadata(path)
        .map_err(|_| "missing guest module")?;
    if !initial.is_file() || initial.file_type().is_symlink() {
        return Err("symlink or unexpected file kind".into());
    }

    #[cfg(unix)]
    let file = {
        use std::os::unix::fs::OpenOptionsExt;
        fs::OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK | libc::O_CLOEXEC)
            .open(path)
            .map_err(|_| "unsafe guest module open")?
    };

    #[cfg(not(unix))]
    let file = File::open(path).map_err(|_| "unreadable guest module")?;

    let opened = file.metadata().map_err(|_| "unreadable guest module")?;
    if !opened.is_file() {
        return Err("unexpected opened file kind".into());
    }

    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        if opened.nlink() != 1 || opened.dev() != initial.dev()
            || opened.ino() != initial.ino() {
            return Err("module identity or link count changed".into());
        }
    }
    Ok((file, opened))
}

fn require_stable_file(before: &fs::Metadata, after: &fs::Metadata) -> Result<(), String> {
    if !after.is_file() || before.len() != after.len() {
        return Err("guest module changed during read".into());
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        if before.dev() != after.dev() || before.ino() != after.ino()
            || before.nlink() != after.nlink()
            || before.mtime() != after.mtime()
            || before.mtime_nsec() != after.mtime_nsec()
            || before.ctime() != after.ctime()
            || before.ctime_nsec() != after.ctime_nsec() {
            return Err("guest module changed during read".into());
        }
    }
    Ok(())
}

fn verify(root: &Path) -> Result<BTreeMap<String, String>, String> {
    real_directory(root)?;
    real_directory(&root.join("contracts"))?;
    let base = root.join(SNAPSHOT);
    real_directory(&base)?;

    let mut entries = Vec::new();
    for entry in fs::read_dir(&base).map_err(|_| "invalid snapshot directory")? {
        let entry = entry.map_err(|_| "unreadable snapshot entry")?;
        let file_type = entry.file_type().map_err(|_| "unreadable snapshot entry")?;
        if !file_type.is_file() || file_type.is_symlink() {
            return Err("symlink or unexpected file kind".into());
        }
        let name = entry.file_name().into_string()
            .map_err(|_| "non-UTF8 snapshot entry name")?;
        entries.push(name);
    }
    entries.sort_unstable();
    let mut expected: Vec<String> = MODULES.iter().map(|(name, _)| (*name).into()).collect();
    expected.sort_unstable();
    if entries != expected {
        return Err("unexpected, missing or legacy duplicate module".into());
    }

    let mut verified = BTreeMap::new();
    for (name, expected_sha) in MODULES {
        let path = base.join(name);
        let (mut file, opened) = open_module(&path)?;
        if opened.len() == 0 || opened.len() > MAX_BYTES as u64 {
            return Err("missing or oversized guest module".into());
        }
        let mut bytes = Vec::new();
        file.by_ref().take((MAX_BYTES + 1) as u64)
            .read_to_end(&mut bytes)
            .map_err(|_| "unreadable guest module")?;
        if bytes.is_empty() || bytes.len() > MAX_BYTES {
            return Err("missing or oversized guest module".into());
        }
        let final_meta = file.metadata().map_err(|_| "unreadable guest module")?;
        require_stable_file(&opened, &final_meta)?;
        let source = std::str::from_utf8(&bytes)
            .map_err(|_| "non-UTF8 guest source")?;
        if source.starts_with('\u{feff}') || source.contains('\0') {
            return Err("BOM or NUL in guest source".into());
        }
        let digest = git_blob_hash(&bytes);
        if digest != expected_sha {
            return Err("canonical .atto Git blob changed".into());
        }
        verified.insert(name.to_string(), digest);
    }
    Ok(verified)
}

fn cli_root() -> Result<PathBuf, String> {
    let mut args = env::args_os().skip(1);
    match args.next() {
        None => Ok(PathBuf::from(".")),
        Some(arg) if arg == "--repository-root" => {
            let root = args.next().ok_or("--repository-root requires a path")?;
            if args.next().is_some() {
                return Err("unexpected extra argument".into());
            }
            Ok(PathBuf::from(root))
        }
        _ => Err("unexpected argument".into()),
    }
}

fn main() {
    let result = cli_root().and_then(|root| verify(&root));
    match result {
        Ok(hashes) => {
            for (name, sha) in hashes {
                println!(
                    "ATTO_CORE_SOURCE_ONLY|{name}|git_blob={sha}|compiler=NOT_EXECUTED"
                );
            }
        }
        Err(reason) => {
            eprintln!("ATTO_CORE_SOURCE_REJECTED={reason}");
            std::process::exit(1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    static NEXT: AtomicU64 = AtomicU64::new(0);

    struct Fixture {
        root: PathBuf,
    }

    impl Fixture {
        fn new() -> Self {
            let stamp = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let suffix = NEXT.fetch_add(1, Ordering::Relaxed);
            let root = env::temp_dir().join(format!(
                "atto-core-rust-gate-{}-{stamp}-{suffix}",
                std::process::id()
            ));
            let snapshot = root.join(SNAPSHOT);
            fs::create_dir_all(&snapshot).unwrap();
            let originals = Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../..")
                .join(SNAPSHOT);
            for (name, _) in MODULES {
                fs::copy(originals.join(name), snapshot.join(name)).unwrap();
            }
            Self { root }
        }

        fn module(&self, name: &str) -> PathBuf {
            self.root.join(SNAPSHOT).join(name)
        }

        fn reject(&self) {
            assert!(verify(&self.root).is_err(), "invalid snapshot accepted");
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.root);
        }
    }

    #[test]
    fn canonical_snapshot_and_git_blob_known_vector() {
        assert_eq!(
            git_blob_hash(b"hello\n"),
            "ce013625030ba8dba906f756967f9e9ca394464a"
        );
        let fixture = Fixture::new();
        let verified = verify(&fixture.root).unwrap();
        assert_eq!(verified.len(), 3);
        for (name, sha) in MODULES {
            assert_eq!(verified[name], sha);
        }
    }

    #[test]
    fn changed_byte_denied() {
        let fixture = Fixture::new();
        let path = fixture.module("numeric.atto");
        let mut bytes = fs::read(&path).unwrap();
        bytes.push(b' ');
        fs::write(path, bytes).unwrap();
        fixture.reject();
    }

    #[test]
    fn missing_source_denied() {
        let fixture = Fixture::new();
        fs::remove_file(fixture.module("numeric.atto")).unwrap();
        fixture.reject();
    }

    #[test]
    fn legacy_duplicate_denied() {
        let fixture = Fixture::new();
        fs::copy(fixture.module("core.atto"), fixture.module("core.ores")).unwrap();
        fixture.reject();
    }

    #[test]
    fn extra_file_denied() {
        let fixture = Fixture::new();
        fs::write(fixture.module("MALICIOUS.atto"), b"guest").unwrap();
        fixture.reject();
    }

    #[test]
    fn oversized_source_denied() {
        let fixture = Fixture::new();
        fs::write(fixture.module("core.atto"), vec![b'A'; MAX_BYTES + 1]).unwrap();
        fixture.reject();
    }

    #[test]
    fn empty_source_denied() {
        let fixture = Fixture::new();
        fs::write(fixture.module("core.atto"), b"").unwrap();
        fixture.reject();
    }

    #[test]
    fn invalid_utf8_denied() {
        let fixture = Fixture::new();
        fs::write(fixture.module("core.atto"), [0xff]).unwrap();
        fixture.reject();
    }

    #[test]
    fn bom_denied() {
        let fixture = Fixture::new();
        let path = fixture.module("core.atto");
        let mut bytes = b"\xef\xbb\xbf".to_vec();
        bytes.extend(fs::read(&path).unwrap());
        fs::write(path, bytes).unwrap();
        fixture.reject();
    }

    #[test]
    fn nul_denied() {
        let fixture = Fixture::new();
        let path = fixture.module("core.atto");
        let mut bytes = fs::read(&path).unwrap();
        bytes.push(0);
        fs::write(path, bytes).unwrap();
        fixture.reject();
    }

    #[cfg(unix)]
    #[test]
    fn module_hardlink_denied() {
        let fixture = Fixture::new();
        let alias = fixture.root.join("outside-core-hardlink");
        fs::hard_link(fixture.module("core.atto"), alias).unwrap();
        fixture.reject();
    }

    #[cfg(unix)]
    #[test]
    fn module_symlink_denied() {
        use std::os::unix::fs::symlink;
        let fixture = Fixture::new();
        fs::remove_file(fixture.module("core.atto")).unwrap();
        symlink(fixture.module("numeric.atto"), fixture.module("core.atto")).unwrap();
        fixture.reject();
    }

    #[cfg(unix)]
    #[test]
    fn snapshot_directory_symlink_denied() {
        use std::os::unix::fs::symlink;
        let fixture = Fixture::new();
        let snapshot = fixture.root.join(SNAPSHOT);
        let shadow = fixture.root.join("other");
        fs::rename(&snapshot, &shadow).unwrap();
        symlink(&shadow, &snapshot).unwrap();
        fixture.reject();
    }

    #[cfg(unix)]
    #[test]
    fn contracts_directory_symlink_denied() {
        use std::os::unix::fs::symlink;
        let fixture = Fixture::new();
        let contracts = fixture.root.join("contracts");
        let shadow = fixture.root.join("other");
        fs::rename(&contracts, &shadow).unwrap();
        symlink(&shadow, &contracts).unwrap();
        fixture.reject();
    }

    #[test]
    fn directory_instead_of_source_denied() {
        let fixture = Fixture::new();
        let source = fixture.module("numeric.atto");
        fs::remove_file(&source).unwrap();
        fs::create_dir(&source).unwrap();
        fixture.reject();
    }
}
