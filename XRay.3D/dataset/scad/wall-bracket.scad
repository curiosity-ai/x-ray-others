// Wall bracket with a gusset, written by Innerscene (CC0 1.0).
// Every value below the Customizer comments can be changed and the viewer
// rebuilds the part.

/* [Bracket] */
// Width of the bracket along the wall, in mm
width = 40; // [20:5:80]
// Length of the shelf arm, in mm
arm = 70; // [40:5:120]
// Height of the wall plate, in mm
plate_height = 60; // [30:5:100]
// Thickness of the plate and arm, in mm
thickness = 6; // [3:1:12]

/* [Screws] */
// Screw hole diameter, in mm
hole = 5; // [3:0.5:8]
// Countersink diameter, in mm
head = 10; // [6:0.5:14]

/* [Quality] */
$fn = 48;

module countersunk_hole(d, head_d, depth) {
    cylinder(d = d, h = depth * 3, center = true);
    translate([0, 0, depth / 2 - head_d / 4 + 0.01])
        cylinder(d1 = d, d2 = head_d, h = (head_d - d) / 2);
}

module plate() {
    difference() {
        // wall plate with rounded top corners
        hull() {
            translate([0, 0, 0]) cube([width, thickness, 1]);
            for (x = [4, width - 4])
                translate([x, 0, plate_height - 4])
                    rotate([-90, 0, 0]) cylinder(r = 4, h = thickness);
        }
        // two countersunk screws, coming in from the front, above the gusset
        for (x = [width * 0.25, width * 0.75])
            translate([x, thickness, plate_height * 0.74])
                rotate([90, 0, 0]) countersunk_hole(hole, head, thickness);
    }
}

module arm_with_gusset() {
    // the shelf arm
    translate([0, 0, 0]) cube([width, arm, thickness]);
    // triangular gusset between the plate and the arm, sunk into both so the faces do not coincide
    translate([width / 2 - thickness / 2, 0, 0])
        rotate([90, 0, 90])
            linear_extrude(height = thickness)
                polygon([[thickness / 2, thickness / 2], [arm * 0.85, thickness / 2], [thickness / 2, plate_height * 0.55]]);
}

color("SteelBlue") plate();
color("LightSteelBlue") arm_with_gusset();
