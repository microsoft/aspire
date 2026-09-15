// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

package com.example.swing;

import javax.swing.SwingUtilities;

public final class SwingLauncher {
    private SwingLauncher() {
    }

    public static void main(String[] args) {
        SwingUtilities.invokeLater(SwingApp::show);
    }
}
