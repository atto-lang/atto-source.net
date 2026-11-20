// Independent Rust-only geometry oracle for .NET F64 inverted class metadata.
// HOST REFERENCE ONLY: not compiled .atto, not native memory, not GC evidence.
use std::collections::HashSet;

const MAX_BYTES: u64 = 16 * 1024 * 1024;

#[derive(Clone, Copy, Debug)]
enum Layout { Aos, Soa, Aosoa }

#[derive(Clone, Copy)]
struct Plan { rows: u64, fields: u64, lanes: u64, layout: Layout, bytes: u64 }

impl Plan {
    fn verify(rows: u64, fields: u64, layout: Layout, lanes: u64) -> Result<Self, &'static str> {
        if rows == 0 || rows > i32::MAX as u64 || fields == 0 || fields > 32 {
            return Err("dimensions");
        }
        if lanes == 0 || lanes > 1024 || !lanes.is_power_of_two() ||
            (!matches!(layout, Layout::Aosoa) && lanes != 1) {
            return Err("lanes");
        }
        let physical = match layout {
            Layout::Aosoa => rows.checked_add(lanes - 1)
                .ok_or("overflow")? / lanes * lanes,
            _ => rows,
        };
        let bytes = physical.checked_mul(fields)
            .and_then(|n| n.checked_mul(8)).ok_or("overflow")?;
        if bytes > MAX_BYTES { return Err("budget"); }
        Ok(Self { rows, fields, lanes, layout, bytes })
    }

    fn offset(&self, row: u64, field: u64) -> Result<u64, &'static str> {
        if row >= self.rows || field >= self.fields {
            return Err("bounds");
        }
        let scalar = match self.layout {
            Layout::Aos => row * self.fields + field,
            Layout::Soa => field * self.rows + row,
            Layout::Aosoa => ((row / self.lanes) * self.fields + field) *
                self.lanes + row % self.lanes,
        };
        let index = scalar.checked_mul(8).ok_or("overflow")?;
        if index.checked_add(8).ok_or("overflow")? > self.bytes {
            return Err("layout");
        }
        Ok(index)
    }
}

#[test]
fn exact_f64_layout_offsets_and_tail_padding() {
    let aos = Plan::verify(17, 3, Layout::Aos, 1).unwrap();
    let soa = Plan::verify(17, 3, Layout::Soa, 1).unwrap();
    let aosoa = Plan::verify(17, 3, Layout::Aosoa, 8).unwrap();
    assert_eq!((aos.bytes, soa.bytes, aosoa.bytes), (408, 408, 576));
    assert_eq!(aos.offset(3, 2), Ok(88));
    assert_eq!(soa.offset(3, 2), Ok(296));
    assert_eq!(aosoa.offset(3, 2), Ok(152));
    assert_eq!(aosoa.offset(16, 2), Ok(512));
    assert_eq!(aosoa.offset(17, 2), Err("bounds"));
    assert_eq!(aosoa.offset(16, 3), Err("bounds"));
}

#[test]
fn offsets_do_not_overlap_and_always_fit_physical_region() {
    for layout in [Layout::Aos, Layout::Soa, Layout::Aosoa] {
        for fields in 1..=8 {
            for rows in 1..=65 {
                let lanes = if matches!(layout, Layout::Aosoa) { 8 } else { 1 };
                let plan = Plan::verify(rows, fields, layout, lanes).unwrap();
                let mut occupied = HashSet::new();
                for r in 0..rows {
                    for f in 0..fields {
                        let index = plan.offset(r, f).unwrap();
                        assert!(index + 8 <= plan.bytes && index % 8 == 0);
                        assert!(occupied.insert(index),
                            "aliased F64 scalar: {layout:?} rows={rows} fields={fields}");
                    }
                }
                assert_eq!(occupied.len(), (rows * fields) as usize);
            }
        }
    }
}

#[test]
fn invalid_layouts_and_oversized_padding_are_denied() {
    assert!(Plan::verify(0, 1, Layout::Aos, 1).is_err());
    assert!(Plan::verify(1, 0, Layout::Aos, 1).is_err());
    assert!(Plan::verify(1, 33, Layout::Aos, 1).is_err());
    assert!(Plan::verify(1, 1, Layout::Aosoa, 0).is_err());
    assert!(Plan::verify(1, 1, Layout::Aosoa, 3).is_err());
    assert!(Plan::verify(1, 1, Layout::Aosoa, 2048).is_err());
    assert!(Plan::verify(1, 1, Layout::Soa, 8).is_err());
    assert!(Plan::verify(i32::MAX as u64 + 1, 1, Layout::Soa, 1).is_err());
    assert_eq!(Plan::verify(699050, 3, Layout::Aos, 1).unwrap().bytes,
        16_777_200);
    assert_eq!(Plan::verify(699050, 3, Layout::Aosoa, 8).err(),
        Some("budget"));
    assert_eq!(Plan::verify(699051, 3, Layout::Aos, 1).err(),
        Some("budget"));
}
