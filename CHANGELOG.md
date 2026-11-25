# Changes

## 2.13.0

- Add optional emission intensity, color temperature and saturation controls to Lighting.
- Update wardrobe menu generation and editor configuration handling.
- Improve CAU batch queues, server information queries, cover preparation and image policies.
- Add SDK editor integration and version-aware build support.

## 2.12.2

- Adjust bust measurement mesh baking to account for inherited renderer scaling in Unity 2022.3.

## 2.12.1

- Add a persistent multi-avatar CAU upload checklist and upload result verification.
- Integrate the checklist with avatar splitting and CAU handoff.
- Refine bust measurement geometry and its editor controls.

## 2.12.0

- Add wardrobe configuration copy/paste and TXT import/export.
- Add configurable generated menu order and persist it with wardrobe settings.
- Improve handling of missing part targets and advanced rules when reading, transferring and repairing menus.
- Preserve existing part objects when repairing their generated components.

## 2.11.3

- Refine wardrobe part cards into a compact 176 px layout.
- Reduce thumbnail size, padding and spacing while retaining tooltips and label truncation.
- Keep generated icons, caches and personal settings unchanged.

## 2.11.2

- Increase wardrobe part cards to 200 px and prevent cards and controls from shrinking.
- Refine thumbnail and category layout, separate the backup badge, and move the vocabulary button below the name field.
- Truncate long labels and preview text while preserving full names in tooltips.
- Keep existing menu icons, caches and personal settings unchanged.

## 2.11.1

- Restore the newer MA menu photo workflow and its preview renderer interface.
- Include the existing Color Overview tools in the package.
- Migrate the eight additional scripts individually to prevent leftover Assets scripts from referencing package-internal classes.
- Preserve generated menu icons, custom images, covers and personal settings in their existing project paths.
- Validate every shipped C# script against its GUID-based legacy migration entry during release builds.

## 2.11.0

- Initial VPM release.
