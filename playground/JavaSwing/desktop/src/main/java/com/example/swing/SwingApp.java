// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

package com.example.swing;

import java.awt.BorderLayout;
import java.awt.event.ActionEvent;
import javax.swing.JButton;
import javax.swing.JFrame;
import javax.swing.JLabel;
import javax.swing.SwingUtilities;
import javax.swing.Timer;

public final class SwingApp {
    private SwingApp() {
    }

    public static void show() {
        JFrame frame = new JFrame("Aspire Swing");
        JLabel label = new JLabel("Swing is running under Aspire");
        JButton button = new JButton("Prove breakpoint");
        button.addActionListener(SwingApp::handleButtonClick);

        frame.setLayout(new BorderLayout());
        frame.add(label, BorderLayout.CENTER);
        frame.add(button, BorderLayout.SOUTH);
        frame.setSize(360, 140);
        frame.setLocationByPlatform(true);
        frame.setDefaultCloseOperation(JFrame.DISPOSE_ON_CLOSE);
        frame.setVisible(true);

        System.out.println("SWING_SAMPLE_STARTED " + label.getText());

        Timer clickTimer = new Timer(1000, ignored -> button.doClick());
        clickTimer.setRepeats(false);
        clickTimer.start();

        Timer exitTimer = new Timer(60000, ignored -> {
            System.out.println("SWING_SAMPLE_EXITING");
            frame.dispose();
        });
        exitTimer.setRepeats(false);
        exitTimer.start();
    }

    static void handleButtonClick(ActionEvent event) {
        JButton source = (JButton) event.getSource();
        source.setText("Breakpoint reached");
        System.out.println("SWING_BUTTON_CLICKED on " + Thread.currentThread().getName());
    }
}
