// Chess rook: a turned profile with battlements and a felt recess.
// Written by Innerscene (CC0 1.0). Units: mm.

/* [Size] */
// Overall height
height = 55; // [35:5:90]
// Base diameter
base_d = 32; // [20:1:50]
// Number of battlement gaps
gaps = 5; // [3:1:8]

/* [Quality] */
$fn = 72;

s = height / 55;
w = base_d / 32;

profile = [
    [0, 0], [16, 0], [16, 3], [15, 4], [15.5, 6], [13, 8], [11, 10],
    [9.5, 14], [8.6, 22], [8.4, 32], [9, 38], [11.5, 40], [12, 42],
    [11, 43], [11, 54], [8.5, 54], [8.5, 50], [0, 50]
];

color("ivory") difference() {
    scale([s * w, s * w, s]) rotate_extrude() polygon(profile);
    for (k = [0:gaps - 1]) rotate(k * 360 / gaps)
        translate([0, -2.4 * s, 48.5 * s]) cube([20 * s * w, 4.8 * s, 10 * s]);
    translate([0, 0, -1]) cylinder(d = 24 * s * w, h = 2);
}
