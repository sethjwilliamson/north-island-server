# Server tests

xunit tests for server rules. Tooling only: never PR this upstream.

```bash
dotnet test tools/server-tests
```

- `EquipmentAlchemyTests` covers:
  - equipment classes (`EquipmentClasses.tsv` + `EquipmentClassesWloHub.tsv`)
  - equipment as a compound result and as a material
  - stone mounts
  - equipment with no known classes
- `AlchemyBaseOrderTests` covers matching on the ordered list of the materials' first classes.
- Set `NORTH_ISLAND_ITEM_DAT` to the game's `Item.dat` to also check the class overlay against the real data. Without it, that test passes without checking anything.
- The tests need the equipment-alchemy-bases changes (SefusJack/north-island-server#18), so they build once tooling has them.
