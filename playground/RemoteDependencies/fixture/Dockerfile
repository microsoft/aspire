FROM docker:29-dind
RUN apk add --no-cache openssh \
    && mkdir -p /root/.ssh /run/sshd \
    && chmod 700 /root/.ssh \
    && passwd -d root
COPY sshd_config /etc/ssh/sshd_config
COPY entrypoint.sh /entrypoint.sh
ENTRYPOINT ["sh", "/entrypoint.sh"]
