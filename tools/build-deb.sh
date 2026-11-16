#!/bin/sh
# Build without root, a network connection, pip, or a Python virtualenv.
set -eu
umask 022

project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
version=${VERSION:-1.4.0}
output_dir=${1:-"$project_dir/dist"}

case "$version" in
    ''|*[!0-9A-Za-z.+:~_-]*) printf '%s\n' 'Invalid Debian version' >&2; exit 2 ;;
esac
case "$version" in
    [0-9]*) ;;
    *) printf '%s\n' 'Debian version must begin with a digit' >&2; exit 2 ;;
esac

command -v dpkg-deb >/dev/null
test -f "$project_dir/linux/fancontroller/cli.py"
test -f "$project_dir/linux/fancontroller/backend.py"
test -f "$project_dir/linux/fancontroller/gui.py"

mkdir -p "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)
stage=$(mktemp -d)
trap 'rm -rf -- "$stage"' EXIT HUP INT TERM
chmod 0755 "$stage"

export SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-$(git -C "$project_dir" log -1 --format=%ct)}
case "$SOURCE_DATE_EPOCH" in
    ''|*[!0-9]*) printf '%s\n' 'Invalid SOURCE_DATE_EPOCH' >&2; exit 2 ;;
esac

install -d "$stage/DEBIAN" "$stage/usr/bin" "$stage/usr/lib/fancontroller/fancontroller" \
    "$stage/usr/libexec" "$stage/usr/share/applications" \
    "$stage/usr/share/icons/hicolor/scalable/apps" \
    "$stage/usr/share/polkit-1/actions" "$stage/usr/share/doc/fancontroller"
sed "s/@VERSION@/$version/" "$project_dir/packaging/debian/control" > "$stage/DEBIAN/control"
chmod 0644 "$stage/DEBIAN/control"
for source in "$project_dir"/linux/fancontroller/*.py; do
    install -m 0644 "$source" "$stage/usr/lib/fancontroller/fancontroller/"
done
install -m 0755 "$project_dir/packaging/debian/fancontroller" "$stage/usr/bin/fancontroller"
install -m 0644 "$project_dir/packaging/debian/launcher.py" "$stage/usr/lib/fancontroller/launcher.py"
install -m 0755 "$project_dir/packaging/debian/fancontroller-helper" "$stage/usr/libexec/fancontroller-helper"
install -m 0644 "$project_dir/packaging/debian/fancontroller.desktop" "$stage/usr/share/applications/"
install -m 0644 "$project_dir/packaging/debian/fancontroller.svg" "$stage/usr/share/icons/hicolor/scalable/apps/"
install -m 0644 "$project_dir/packaging/debian/org.fancontroller.control.policy" "$stage/usr/share/polkit-1/actions/"
install -m 0644 "$project_dir/packaging/debian/copyright" "$stage/usr/share/doc/fancontroller/"
install -m 0644 "$project_dir/README.md" "$stage/usr/share/doc/fancontroller/README.md"
gzip -n -9 -c "$project_dir/packaging/debian/changelog" > "$stage/usr/share/doc/fancontroller/changelog.gz"
printf 'Installed-Size: %s\n' "$(du --apparent-size -sk "$stage/usr" | cut -f 1)" >> "$stage/DEBIAN/control"
(cd "$stage" && find usr -type f -print0 | LC_ALL=C sort -z | xargs -0 md5sum > DEBIAN/md5sums)

# Root ownership and normalized mtimes make identical sources reproducible.
find "$stage" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
package="$output_dir/fancontroller_${version}_all.deb"
dpkg-deb --root-owner-group -Zxz --build "$stage" "$package"
(cd "$output_dir" && sha256sum "fancontroller_${version}_all.deb" > SHA256SUMS)
printf 'Built %s\n' "$package"
