// Twisted vase, written by Innerscene (CC0 1.0).
// A star-shaped section is twisted and tapered up the height of the vase
// and sits on a turned foot.

/* [Vase] */
// Height of the twisted body, in mm
height = 110; // [60:5:200]
// Number of points on the star
points = 8; // [5:1:12]
// Outer radius at the base, in mm
outer = 32; // [20:1:50]
// Inner radius at the base, in mm
inner = 24; // [12:1:40]
// Twist over the full height, in degrees
twist = 120; // [0:15:360]
// Wall thickness, in mm
wall = 2.4; // [1.2:0.2:4]

/* [Quality] */
$fn = 64;

function star(n, ro, ri) = [
    for (i = [0 : 2 * n - 1])
        let (a = 180 * i / n, r = (i % 2 == 0) ? ro : ri)
            [r * cos(a), r * sin(a)]
];

module body() {
    difference() {
        linear_extrude(height = height, twist = twist, slices = 60, scale = 0.7)
            polygon(star(points, outer, inner));
        // hollow it, leaving a floor
        translate([0, 0, wall * 2])
            linear_extrude(height = height, twist = twist, slices = 60, scale = 0.7)
                offset(delta = -wall) polygon(star(points, outer, inner));
    }
}

module foot() {
    // turned foot: a profile revolved around the vertical axis
    rotate_extrude()
        polygon([[0, 0], [outer * 0.9, 0], [outer * 0.96, 3], [outer * 0.8, 8], [0, 8]]);
}

color("Teal") translate([0, 0, 8]) body();
color("DimGray") foot();
