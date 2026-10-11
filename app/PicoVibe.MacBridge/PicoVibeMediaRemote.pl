#!/usr/bin/perl
# Copyright (c) 2026 Pico Vibe contributors.
# Loads the bundled command shim inside Apple's platform-signed Perl process.

use strict;
use warnings;
use DynaLoader;

my $library = shift @ARGV or die "MediaRemote shim path is required\n";
die "Unexpected MediaRemote shim arguments\n" if @ARGV;

my $handle = DynaLoader::dl_load_file($library, 0)
  or die "Unable to load MediaRemote shim: $library\n";
my $symbol = DynaLoader::dl_find_symbol($handle, "pico_vibe_next_track")
  or die "MediaRemote next-track symbol is missing\n";

DynaLoader::dl_install_xsub("main::pico_vibe_next_track", $symbol);
pico_vibe_next_track();
