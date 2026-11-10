use std::io;

pub type Result<T, E = Error> = std::result::Result<T, E>;

/// Every update-system failure. Hash and signature failures are always fatal (fail closed).
#[derive(Debug)]
pub enum Error {
    /// Content did not hash to the value the manifest promised.
    HashMismatch { key: String, expected: String, actual: String },
    /// No mirror could supply the key (a 404 everywhere, or every mirror failed: `source` is the last).
    NotFound { key: String, source: Option<Box<Error>> },
    /// A manifest's signature did not verify against the trust anchors.
    Signature(&'static str),
    /// A manifest declared a path that escapes its root (zip-slip / traversal).
    UnsafePath(String),
    /// A mirror answered with an error status.
    Status { url: String, status: u16 },
    /// Transport: connect, TLS, a response or a read that timed out.
    Http(String),
    Io(io::Error),
    Json(serde_json::Error),
    Cancelled,
    /// Any other engine failure (a downgrade, a dependency cycle, a malformed manifest, ...).
    Other(String),
}

impl Error {
    pub fn other(message: impl Into<String>) -> Self {
        Error::Other(message.into())
    }

    /// A failure another attempt or another mirror may not repeat: a network blip or a corrupt
    /// object. A real 404 is not.
    pub fn is_transient(&self) -> bool {
        match self {
            Error::HashMismatch { .. } | Error::Http(_) | Error::Io(_) | Error::Status { .. } => true,
            Error::NotFound { source: Some(inner), .. } => inner.is_transient(),
            _ => false,
        }
    }
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Error::HashMismatch { key, expected, actual } => {
                write!(f, "hash mismatch for '{key}': expected {expected}, got {actual}")
            }
            Error::NotFound { key, source: None } => write!(f, "content not found on any mirror: '{key}'"),
            Error::NotFound { key, source: Some(last) } => {
                write!(f, "content not found on any mirror: '{key}' (last error: {last})")
            }
            Error::Signature(what) => write!(f, "signature verification failed for {what}"),
            Error::UnsafePath(path) => write!(f, "unsafe path rejected: '{path}'"),
            Error::Status { url, status } => write!(f, "{url} answered {status}"),
            Error::Http(message) => f.write_str(message),
            Error::Io(e) => write!(f, "{e}"),
            Error::Json(e) => write!(f, "invalid json: {e}"),
            Error::Cancelled => f.write_str("cancelled"),
            Error::Other(message) => f.write_str(message),
        }
    }
}

impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Error::Io(e) => Some(e),
            Error::Json(e) => Some(e),
            Error::NotFound { source: Some(inner), .. } => Some(inner.as_ref()),
            _ => None,
        }
    }
}

impl From<io::Error> for Error {
    fn from(e: io::Error) -> Self {
        Error::Io(e)
    }
}

impl From<serde_json::Error> for Error {
    fn from(e: serde_json::Error) -> Self {
        Error::Json(e)
    }
}
