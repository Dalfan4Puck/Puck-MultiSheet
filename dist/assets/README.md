# Assets

MultiSheet does **not** ship Unity AssetBundles for rinks or props.

- Default setup clones the base-game rink in code (`UseAssetBundle: false`).
- Do **not** place SkatePark, launch-ramp, or other third-party bundles here.
- `dist/assets` is README-only; workshop assemble strips anything else.

Legacy optional path (unused for PHL): `UseAssetBundle: true` + a custom `puckobjects` bundle for Jake-Porter open-world TestLevel — not the hockey rink, and not stored in this repo.
