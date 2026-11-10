//! Mirror-relative keys, shared with the publisher and the worker (`src/CaligoExpress.Cloud/src/keys.ts`).
//! The default app keeps the root layout shipped bootstrappers hard-code; every other app gets the same
//! scheme under `apps/<appId>/`.

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct KeyScheme {
    prefix: String,
}

impl KeyScheme {
    /// The root layout for `None` or an empty id, else `apps/<appId>/`.
    pub fn for_app(app_id: Option<&str>) -> Self {
        match app_id.filter(|id| !id.is_empty()) {
            Some(id) => Self { prefix: format!("apps/{id}/") },
            None => Self::default(),
        }
    }

    pub fn prefix(&self) -> &str {
        &self.prefix
    }

    pub fn config(&self) -> String {
        format!("{}config.json", self.prefix)
    }

    pub fn release(&self, channel: &str) -> String {
        format!("{}{channel}/release.json", self.prefix)
    }

    pub fn bootstrapper_manifest(&self, channel: &str) -> String {
        format!("{}{channel}/bootstrapper.json", self.prefix)
    }

    pub fn package_manifest(&self, name: &str, version: &str) -> String {
        format!("{}{name}/{version}/package.json", self.prefix)
    }

    /// A content-addressed chunk, sharded by its first hash byte.
    pub fn chunk(&self, hash: &str) -> String {
        format!("{}chunks/{}/{hash}", self.prefix, &hash[..2.min(hash.len())])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_default_app_keeps_the_root_layout_and_others_get_a_prefix() {
        let root = KeyScheme::for_app(None);
        assert_eq!(root.config(), "config.json");
        assert_eq!(root.release("beta"), "beta/release.json");
        assert_eq!(root.chunk("abcdef"), "chunks/ab/abcdef");
        assert_eq!(KeyScheme::for_app(Some("")), root);
        let app = KeyScheme::for_app(Some("demo"));
        assert_eq!(app.bootstrapper_manifest("live"), "apps/demo/live/bootstrapper.json");
        assert_eq!(app.package_manifest("core", "1.0.0"), "apps/demo/core/1.0.0/package.json");
    }
}
